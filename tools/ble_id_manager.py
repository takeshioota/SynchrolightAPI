"""BLE ID管理ツール - 複数台のID読み取り・連番書き込み"""
import sys
sys.stdout.reconfigure(encoding='utf-8')
import asyncio
from bleak import BleakScanner, BleakClient

WRITE_CHAR = '0000fff5-0000-1000-8000-00805f9b34fb'
NOTIFY_CHAR = '0000fff4-0000-1000-8000-00805f9b34fb'


def calc_checksum(data):
    return sum(data) & 0xFF


def build_query_cmd():
    """ID照会コマンド: FB E2 [checksum]"""
    cmd = bytes([0xFB, 0xE2])
    return cmd + bytes([calc_checksum(cmd)])


def build_write_cmd(session, row, col):
    """ID書き込みコマンド: FB E1 05 [session] [row_hi] [row_lo] [col_hi] [col_lo] [checksum]"""
    cmd = bytes([
        0xFB, 0xE1, 0x05,
        session & 0xFF,
        (row >> 8) & 0xFF, row & 0xFF,
        (col >> 8) & 0xFF, col & 0xFF,
    ])
    return cmd + bytes([calc_checksum(cmd)])


def parse_id_response(data):
    """FC E2 05 [session] [row_hi] [row_lo] [col_hi] [col_lo] [checksum] をパース"""
    if len(data) < 8 or data[0] != 0xFC or data[1] != 0xE2:
        return None
    session = data[3]
    row = (data[4] << 8) | data[5]
    col = (data[6] << 8) | data[7]
    return {'session': session, 'row': row, 'col': col}


def build_blink_cmd(r, g, b):
    """確認用点滅コマンド"""
    cmd = bytes([0xFB, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
                 r, g, b, 0x00, 0x01, 0xF4])
    return cmd + bytes([calc_checksum(cmd)])


async def scan_blelights(timeout=8.0):
    """全BLELightデバイスをスキャン"""
    print(f"  BLEスキャン中... ({timeout}秒)")
    devices = await BleakScanner.discover(timeout=timeout, return_adv=True)
    found = []
    for addr, (dev, adv) in devices.items():
        if dev.name and 'BLELight' in dev.name:
            found.append((dev.address, dev.name, adv.rssi))
    found.sort(key=lambda x: x[2], reverse=True)
    return found


async def read_id(addr):
    """1台のIDを読み取る"""
    result = None

    def on_notify(sender, data):
        nonlocal result
        if len(data) >= 2 and data[0] == 0xFC and data[1] == 0xE2:
            result = parse_id_response(data)

    async with BleakClient(addr, timeout=15.0) as client:
        await client.start_notify(NOTIFY_CHAR, on_notify)
        await client.write_gatt_char(WRITE_CHAR, build_query_cmd())
        await asyncio.sleep(2.0)

    return result


async def write_id(addr, session, row, col):
    """1台にIDを書き込み、照会で確認"""
    write_ok = False
    read_back = None

    def on_notify(sender, data):
        nonlocal write_ok, read_back
        if len(data) >= 3 and data[0] == 0xFC and data[1] == 0xE1:
            write_ok = data[2] == 0x01
        if len(data) >= 2 and data[0] == 0xFC and data[1] == 0xE2:
            read_back = parse_id_response(data)

    cmd = build_write_cmd(session, row, col)

    async with BleakClient(addr, timeout=15.0) as client:
        await client.start_notify(NOTIFY_CHAR, on_notify)

        # 書き込み
        await client.write_gatt_char(WRITE_CHAR, cmd)
        await asyncio.sleep(1.5)

        # 照会で確認
        await client.write_gatt_char(WRITE_CHAR, build_query_cmd())
        await asyncio.sleep(1.5)

        # 成功なら青点滅
        if write_ok:
            await client.write_gatt_char(WRITE_CHAR, build_blink_cmd(0, 0, 255))

    return write_ok, read_back


# ── メニュー1: 全台ID読み取り ──────────────────────
async def menu_read_all():
    """周辺の全BLELightのIDを読み取って一覧表示"""
    devices = await scan_blelights(timeout=10.0)
    if not devices:
        print("  BLELightが見つかりません。")
        return

    print(f"  {len(devices)}台 発見\n")
    print(f"  {'#':>3}  {'アドレス':<20} {'RSSI':>5}  {'セッション':>6}  {'行':>5}  {'列':>5}  {'状態'}")
    print(f"  {'---':>3}  {'--------------------':<20} {'-----':>5}  {'------':>6}  {'-----':>5}  {'-----':>5}  {'----'}")

    for i, (addr, name, rssi) in enumerate(devices):
        try:
            id_info = await read_id(addr)
            if id_info:
                print(f"  {i+1:3d}  {addr:<20} {rssi:5d}  {id_info['session']:6d}  {id_info['row']:5d}  {id_info['col']:5d}  OK")
            else:
                print(f"  {i+1:3d}  {addr:<20} {rssi:5d}  {'?':>6}  {'?':>5}  {'?':>5}  応答なし")
        except Exception as e:
            print(f"  {i+1:3d}  {addr:<20} {rssi:5d}  {'?':>6}  {'?':>5}  {'?':>5}  エラー: {e}")
        await asyncio.sleep(0.3)


# ── メニュー2: 1台ずつID読み取り ──────────────────────
async def menu_read_one():
    """1台ずつ近づけてID読み取り"""
    count = 0
    while True:
        input(f"\n  端末を近づけてEnter (qで終了): ")
        # qチェックは input の戻り値で
        devices = await scan_blelights(timeout=6.0)
        if not devices:
            print("  BLELightが見つかりません。もう一度近づけてください。")
            continue

        addr, name, rssi = devices[0]
        count += 1
        try:
            id_info = await read_id(addr)
            if id_info:
                print(f"  #{count} {addr} セッション={id_info['session']} 行={id_info['row']} 列={id_info['col']}")
            else:
                print(f"  #{count} {addr} ID照会の応答なし")
        except Exception as e:
            print(f"  #{count} {addr} エラー: {e}")


# ── メニュー3: 連番書き込み ──────────────────────
async def menu_write_sequential():
    """複数台に連番IDを書き込み"""
    print("\n  --- 連番ID書き込み設定 ---")
    try:
        session = int(input("  セッション (1-255) [1]: ").strip() or "1")
        start_row = int(input("  開始行アドレス (1-65535) [1]: ").strip() or "1")
        col = int(input("  列アドレス (1-65535) [1]: ").strip() or "1")
        count = int(input("  台数 [50]: ").strip() or "50")
        increment = int(input("  行の増分 [1]: ").strip() or "1")
    except ValueError:
        print("  入力エラー。数値を入力してください。")
        return

    print(f"\n  設定: セッション={session}, 開始行={start_row}, 列={col}, 台数={count}, 増分={increment}")
    print(f"  行範囲: {start_row} ~ {start_row + (count-1) * increment}")
    confirm = input("  開始しますか？ (y/n): ").strip().lower()
    if confirm != 'y':
        print("  キャンセルしました。")
        return

    results = []
    for i in range(count):
        row = start_row + i * increment
        print(f"\n  --- #{i+1}/{count}  セッション={session} 行={row} 列={col} ---")
        resp = input("  端末を近づけてEnter (sでスキップ, qで中断): ").strip().lower()
        if resp == 'q':
            print("  中断しました。")
            break
        if resp == 's':
            results.append((row, 'スキップ', None))
            continue

        devices = await scan_blelights(timeout=6.0)
        if not devices:
            print("  BLELightが見つかりません。")
            retry = input("  リトライ？ (y/n): ").strip().lower()
            if retry == 'y':
                devices = await scan_blelights(timeout=10.0)
            if not devices:
                results.append((row, '未検出', None))
                continue

        addr, name, rssi = devices[0]
        print(f"  検出: {addr} (RSSI:{rssi})")

        try:
            ok, read_back = await write_id(addr, session, row, col)
            if ok and read_back:
                verified = (read_back['session'] == session and
                            read_back['row'] == row and
                            read_back['col'] == col)
                if verified:
                    print(f"  書き込み成功 (照会確認OK, 青点滅)")
                    results.append((row, '成功', addr))
                else:
                    print(f"  書き込み応答OK だが照会不一致: {read_back}")
                    results.append((row, '不一致', addr))
            elif ok:
                print(f"  書き込み応答OK (照会応答なし)")
                results.append((row, '応答OK', addr))
            else:
                print(f"  書き込み応答なし")
                results.append((row, '応答なし', addr))
        except Exception as e:
            print(f"  エラー: {e}")
            results.append((row, f'エラー', addr))

    # 結果サマリ
    print(f"\n  {'='*50}")
    print(f"  書き込み結果サマリ (セッション={session}, 列={col})")
    print(f"  {'='*50}")
    print(f"  {'#':>3}  {'行':>5}  {'状態':<10}  {'アドレス'}")
    for idx, (row, status, addr) in enumerate(results):
        print(f"  {idx+1:3d}  {row:5d}  {status:<10}  {addr or '-'}")

    ok_count = sum(1 for _, s, _ in results if s == '成功')
    print(f"\n  合計: {ok_count}/{len(results)} 台成功")


# ── メインメニュー ──────────────────────
async def main():
    print("=" * 50)
    print("BLE ID管理ツール")
    print("=" * 50)

    while True:
        print("\n  1. 周辺の全端末のIDを読み取り")
        print("  2. 1台ずつID読み取り")
        print("  3. 連番ID書き込み")
        print("  q. 終了")
        choice = input("\n  選択: ").strip().lower()

        if choice == '1':
            await menu_read_all()
        elif choice == '2':
            await menu_read_one()
        elif choice == '3':
            await menu_write_sequential()
        elif choice == 'q':
            print("  終了します。")
            break
        else:
            print("  1, 2, 3, q のいずれかを入力してください。")


if __name__ == '__main__':
    asyncio.run(main())
