"""BLE点灯テスト - スキャンで見つかった全BLELight端末に順次RGB送信"""
import sys
sys.stdout.reconfigure(encoding='utf-8')
import asyncio
from bleak import BleakScanner, BleakClient

WRITE_CHAR = '0000fff5-0000-1000-8000-00805f9b34fb'
NOTIFY_CHAR = '0000fff4-0000-1000-8000-00805f9b34fb'


def calc_checksum(data):
    return sum(data) & 0xFF


def build_rgb_cmd(r, g, b, power=1):
    """BLE RGB点灯コマンド: FB 01 00 00 00 00 00 [power] [R] [G] [B] 00 00 [checksum]"""
    cmd = bytes([0xFB, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
                 power, r, g, b, 0x00, 0x00])
    cs = calc_checksum(cmd)
    return cmd + bytes([cs])


def build_off_cmd():
    """消灯コマンド"""
    return build_rgb_cmd(0, 0, 0, 0)


async def scan_all_blelights(timeout=10.0):
    """全BLELightデバイスをスキャン"""
    print(f"BLEスキャン中... ({timeout}秒)")
    devices = await BleakScanner.discover(timeout=timeout, return_adv=True)
    found = []
    for addr, (dev, adv) in devices.items():
        if dev.name and 'BLELight' in dev.name:
            found.append((dev.address, dev.name, adv.rssi))
    found.sort(key=lambda x: x[2], reverse=True)  # RSSI順
    return found


async def send_rgb_to_device(addr, name, r, g, b, duration=5.0):
    """1台にRGB送信して一定時間点灯"""
    try:
        async with BleakClient(addr, timeout=10.0) as client:
            cmd = build_rgb_cmd(r, g, b)
            await client.write_gatt_char(WRITE_CHAR, cmd)
            print(f"  ✅ {name} ({addr}) → RGB({r},{g},{b}) 送信OK")
            return True
    except Exception as e:
        print(f"  ❌ {name} ({addr}) → 接続失敗: {e}")
        return False


async def main():
    print("=" * 50)
    print("BLE点灯テスト")
    print("=" * 50)

    # 全端末スキャン
    devices = await scan_all_blelights(timeout=12.0)
    print(f"\n発見: {len(devices)}台のBLELight")

    if not devices:
        print("❌ BLELightが見つかりません。端末の電源を確認してください。")
        return

    for i, (addr, name, rssi) in enumerate(devices):
        print(f"  {i+1}. {name} ({addr}) RSSI:{rssi}")

    # 全台に緑を送信
    print(f"\n--- 全{len(devices)}台に緑色を送信 ---")
    success = 0
    fail = 0
    for addr, name, rssi in devices:
        ok = await send_rgb_to_device(addr, name, 0, 255, 0)
        if ok:
            success += 1
        else:
            fail += 1
        await asyncio.sleep(0.5)

    print(f"\n結果: {success}台成功 / {fail}台失敗 (全{len(devices)}台)")

    # 確認待ち
    input("\n端末の点灯を確認してください。Enterで次のテスト（赤）...")

    # 赤に変更
    print(f"\n--- 全{len(devices)}台に赤色を送信 ---")
    for addr, name, rssi in devices:
        await send_rgb_to_device(addr, name, 255, 0, 0)
        await asyncio.sleep(0.5)

    input("\nEnterで次のテスト（青）...")

    # 青に変更
    print(f"\n--- 全{len(devices)}台に青色を送信 ---")
    for addr, name, rssi in devices:
        await send_rgb_to_device(addr, name, 0, 0, 255)
        await asyncio.sleep(0.5)

    input("\nEnterで消灯...")

    # 消灯
    print(f"\n--- 全{len(devices)}台を消灯 ---")
    off_cmd = build_off_cmd()
    for addr, name, rssi in devices:
        try:
            async with BleakClient(addr, timeout=10.0) as client:
                await client.write_gatt_char(WRITE_CHAR, off_cmd)
                print(f"  ✅ {name} ({addr}) 消灯OK")
        except Exception as e:
            print(f"  ❌ {name} ({addr}) 消灯失敗: {e}")
        await asyncio.sleep(0.5)

    print("\nテスト完了！")


if __name__ == '__main__':
    asyncio.run(main())
