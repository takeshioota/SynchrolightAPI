"""BLE点滅テスト - 1台ずつ接続して赤3回→緑3回→青3回点滅"""
import sys
sys.stdout.reconfigure(encoding='utf-8')
import asyncio
from bleak import BleakScanner, BleakClient

WRITE_CHAR = '0000fff5-0000-1000-8000-00805f9b34fb'


def calc_checksum(data):
    return sum(data) & 0xFF


def build_rgb_cmd(r, g, b, power=1):
    cmd = bytes([0xFB, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
                 power, r, g, b, 0x00, 0x00])
    return cmd + bytes([calc_checksum(cmd)])


async def blink_sequence(client, r, g, b, count=3, on_ms=0.4, off_ms=0.3):
    """指定色でcount回点滅"""
    on_cmd = build_rgb_cmd(r, g, b)
    off_cmd = build_rgb_cmd(0, 0, 0, 0)
    for _ in range(count):
        await client.write_gatt_char(WRITE_CHAR, on_cmd)
        await asyncio.sleep(on_ms)
        await client.write_gatt_char(WRITE_CHAR, off_cmd)
        await asyncio.sleep(off_ms)


async def test_one_device(addr, name, index, total):
    """1台に赤3回→緑3回→青3回"""
    try:
        async with BleakClient(addr, timeout=10.0) as client:
            print(f"  [{index}/{total}] {name} ({addr}) 接続OK → 点滅中...", end="", flush=True)
            # 赤3回
            await blink_sequence(client, 255, 0, 0)
            # 緑3回
            await blink_sequence(client, 0, 255, 0)
            # 青3回
            await blink_sequence(client, 0, 0, 255)
            print(" ✅ 完了")
            return True
    except Exception as e:
        print(f" ❌ 失敗: {e}")
        return False


async def main():
    print("=" * 50)
    print("BLE点滅テスト (赤3回→緑3回→青3回)")
    print("=" * 50)

    # スキャン
    print("BLEスキャン中... (12秒)")
    devices = await BleakScanner.discover(timeout=12.0, return_adv=True)
    found = []
    for addr, (dev, adv) in devices.items():
        if dev.name and 'BLELight' in dev.name:
            found.append((dev.address, dev.name, adv.rssi))
    found.sort(key=lambda x: x[2], reverse=True)

    print(f"発見: {len(found)}台")
    if not found:
        print("❌ BLELightが見つかりません")
        return

    # 5秒待機
    print("\n5秒後にテスト開始...")
    await asyncio.sleep(5)

    print(f"\n--- 全{len(found)}台に順次接続して点滅テスト ---")
    success = 0
    fail = 0
    failed_devices = []

    for i, (addr, name, rssi) in enumerate(found, 1):
        ok = await test_one_device(addr, name, i, len(found))
        if ok:
            success += 1
        else:
            fail += 1
            failed_devices.append((addr, name))
        await asyncio.sleep(0.3)

    print(f"\n{'=' * 50}")
    print(f"結果: {success}台成功 / {fail}台失敗 (全{len(found)}台)")
    if failed_devices:
        print("失敗した端末:")
        for addr, name in failed_devices:
            print(f"  - {name} ({addr})")

    # 失敗した端末をリトライ
    if failed_devices:
        print(f"\n--- 失敗した{len(failed_devices)}台をリトライ ---")
        retry_success = 0
        for addr, name in failed_devices:
            ok = await test_one_device(addr, name, "R", len(failed_devices))
            if ok:
                retry_success += 1
            await asyncio.sleep(0.5)
        print(f"リトライ結果: {retry_success}台追加成功")
        success += retry_success
        fail -= retry_success

    print(f"\n最終結果: {success}台成功 / {fail}台失敗 (全{len(found)}台)")


if __name__ == '__main__':
    asyncio.run(main())
