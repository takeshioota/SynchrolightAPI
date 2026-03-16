"""BLE ID書き込みツール - 1台ずつ対話的に書き込み"""
import sys
sys.stdout.reconfigure(encoding='utf-8')
import asyncio
from bleak import BleakScanner, BleakClient

WRITE_CHAR = '0000fff5-0000-1000-8000-00805f9b34fb'
NOTIFY_CHAR = '0000fff4-0000-1000-8000-00805f9b34fb'

# 50台分のID (H列の値)
IDS = [
    "fce2050100010001e6",
    "fce2050100020001e7",
    "fce2050100030001e8",
    "fce2050100040001e9",
    "fce2050100050001ea",
    "fce2050100060001eb",
    "fce2050100070001ec",
    "fce2050100080001ed",
    "fce2050100090001ee",
    "fce20501000a0001ef",
    "fce20501000b0001f0",
    "fce20501000c0001f1",
    "fce20501000d0001f2",
    "fce20501000e0002f3",
    "fce20501000f0003f4",
    "fce2050100100001f5",
    "fce2050100110001f6",
    "fce2050100120001f7",
    "fce2050100130001f8",
    "fce2050100140001f9",
    "fce2050100150001fa",
    "fce2050100160001fb",
    "fce2050100070001fc",
    "fce2050100180001fd",
    "fce2050100190001fe",
    "fce20501001a0001ff",
    "fce20501001b000100",
    "fce20501001c000101",
    "fce20501001d000102",
    "fce20501001e000103",
    "fce20501001f000104",
    "fce205010020000105",
    "fce205010021000106",
    "fce205010022000107",
    "fce205010023000108",
    "fce205010024000109",
    "fce20501002500010a",
    "fce20501002600010b",
    "fce20501002700010c",
    "fce20501002800010d",
    "fce20501002900010e",
    "fce20501002a00010f",
    "fce20501002b000110",
    "fce20501002c000111",
    "fce20501002d000112",
    "fce20501002e000113",
    "fce20501002f000114",
    "fce205010030000115",
    "fce205010031000116",
    "fce205010032000117",
]


def calc_checksum(data):
    return sum(data) & 0xFF


async def scan_blelight():
    """BLELightデバイスをスキャン"""
    devices = await BleakScanner.discover(timeout=8.0, return_adv=True)
    for addr, (dev, adv) in devices.items():
        if dev.name and 'BLELight' in dev.name:
            return dev.address
    return None


async def write_id(addr, id_hex, index):
    """IDを書き込む"""
    # ID hexからE1コマンドを構築
    # 元データ: FC E2 05 01 00 29 00 01 0E (照会応答)
    # 書き込み: FB E1 05 01 00 29 00 01 チェックサム
    # id_hexの先頭2バイト(fce2)はレスポンスヘッダなので、3バイト目以降がIDデータ
    id_bytes = bytes.fromhex(id_hex)
    # id_bytes = FC E2 05 01 00 xx 00 xx checksum
    # IDデータ部分: id_bytes[2:-1] = 05 01 00 xx 00 xx
    id_data = id_bytes[2:-1]  # チェックサム除いたIDデータ部分

    # 書き込みコマンド: FB E1 + IDデータ + チェックサム
    cmd = bytes([0xFB, 0xE1]) + id_data
    cs = calc_checksum(cmd)
    cmd_full = cmd + bytes([cs])

    write_success = False

    def on_notify(sender, data):
        nonlocal write_success
        hex_str = data.hex()
        if hex_str.startswith('fce1'):
            if data[2] == 0x01:
                write_success = True

    async with BleakClient(addr, timeout=15.0) as client:
        await client.start_notify(NOTIFY_CHAR, on_notify)

        # ID書き込み
        await client.write_gatt_char(WRITE_CHAR, cmd_full)
        await asyncio.sleep(1.5)

        # 書き込み確認: ID照会
        query = bytes([0xFB, 0xE2])
        query_full = query + bytes([calc_checksum(query)])
        await client.write_gatt_char(WRITE_CHAR, query_full)
        await asyncio.sleep(1.5)

        # 成功なら青点滅で通知
        if write_success:
            blink_cmd = bytes([0xFB, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
                               0x00, 0x00, 0xFF, 0x00, 0x01, 0xF4])
            blink_cs = calc_checksum(blink_cmd)
            await client.write_gatt_char(WRITE_CHAR, blink_cmd + bytes([blink_cs]))

    return write_success, cmd_full.hex()


async def process_one(index):
    """1台分の処理"""
    id_hex = IDS[index]
    print(f"\n--- #{index+1}/50 ID: {id_hex} ---")
    print("BLELightをスキャン中...")

    addr = await scan_blelight()
    if not addr:
        print("❌ BLELightが見つかりません。端末の電源を確認してください。")
        return False

    print(f"✓ BLELight発見: {addr}")
    print(f"  書き込みID: {id_hex}")

    success, cmd_hex = await write_id(addr, id_hex, index)

    if success:
        print(f"✅ #{index+1} 書き込み成功！(青点滅で確認)")
    else:
        print(f"⚠️ #{index+1} 書き込み応答未確認（端末の青点滅を確認してください）")
    print(f"  送信コマンド: {cmd_hex}")
    return True
