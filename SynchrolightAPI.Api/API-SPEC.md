# SynchrolightAPI 仕様書

Base URL: `http://localhost:5100`

全レスポンスは以下の共通フォーマットで返却される。

```json
{
  "success": true,
  "message": "操作結果の説明",
  "error": "エラー時のみ",
  "data": {}
}
```

## Color 指定方法

Color フィールドは以下の2形式に対応する。

```json
// RGB オブジェクト
{ "r": 255, "g": 0, "b": 128 }

// プリセット文字列
"red" | "green" | "blue" | "white" | "black"
```

---

## 1. Transport（COM ポート管理）

### GET /api/transport/scan

利用可能な COM ポート一覧を取得する。

**レスポンス:**
```json
{
  "success": true,
  "data": { "ports": ["COM3", "COM4", "COM5"] }
}
```

### POST /api/transport/connect

COM ポートに接続する。

**リクエスト:**
```json
{ "portNames": ["COM3", "COM4"] }
```

### POST /api/transport/disconnect

全ポートを切断する。

### GET /api/transport/status

接続状態とキュー長を取得する。

**レスポンス:**
```json
{
  "success": true,
  "data": {
    "queueLength": 0,
    "highPriorityQueueLength": 0,
    "connectedPorts": 2,
    "disconnectedPorts": 0,
    "lastError": null,
    "ports": [...]
  }
}
```

### POST /api/transport/keepalive

Base64 エンコードされたパケットを再送する。キープアライブ用途。

**リクエスト:**
```json
{ "base64Packet": "ogD/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAE=" }
```

---

## 2. Transmitter（送信機設定）

### POST /api/transmitter/init

チャネルと電力を同時に設定する（FA + FB コマンド）。

**リクエスト:**
```json
{ "channel": 4, "power": 0 }
```

| パラメータ | 範囲 |
|-----------|------|
| channel   | 1〜4 |
| power     | 0〜3 |

### POST /api/transmitter/set-channel

チャネルのみ設定する（FA コマンド）。高優先キューで送信。

**リクエスト:**
```json
{ "channel": 4 }
```

### POST /api/transmitter/set-power

電力のみ設定する（FB コマンド）。高優先キューで送信。

**リクエスト:**
```json
{ "power": 0 }
```

---

## 3. Light（照明制御）

### POST /api/light/global

全体の色を設定する（A2 コマンド）。

**リクエスト:**
```json
{ "color": { "r": 255, "g": 0, "b": 0 } }
```

### POST /api/light/off

全消灯する（A2 で黒を送信）。リクエストボディ不要。

### POST /api/light/points

座標指定で色を設定する（A0 コマンド）。

**リクエスト（単色）:**
```json
{ "field": 0, "startRow": 1, "startCol": 1, "len": 3, "color": { "r": 255, "g": 0, "b": 0 } }
```

**リクエスト（個別色）:**
```json
{ "field": 0, "startRow": 1, "startCol": 1, "colors": [{ "r": 255, "g": 0, "b": 0 }, { "r": 0, "g": 255, "b": 0 }] }
```

### POST /api/light/rows

複数行を同色で設定する（AA コマンド）。

**リクエスト:**
```json
{ "field": 0, "startRow": 1, "rowLen": 3, "color": { "r": 255, "g": 0, "b": 0 } }
```

### POST /api/light/rows/each

行制御（A3 コマンド）。8行ごとに自動分割して送信する。

**リクエスト（単色）:**
```json
{ "field": 0, "startRow": 1, "len": 5, "color": { "r": 255, "g": 0, "b": 0 } }
```

**リクエスト（個別色）:**
```json
{ "field": 0, "startRow": 1, "colors": [{ "r": 255, "g": 0, "b": 0 }, { "r": 0, "g": 255, "b": 0 }] }
```

### POST /api/light/cols

複数列を同色で設定する（A8 コマンド）。

**リクエスト:**
```json
{ "field": 0, "startCol": 1, "colLen": 3, "color": { "r": 255, "g": 0, "b": 0 } }
```

### POST /api/light/cols/each

列制御（A4 コマンド）。

**リクエスト（単色）:**
```json
{ "field": 0, "startCol": 1, "len": 5, "color": { "r": 255, "g": 0, "b": 0 } }
```

**リクエスト（個別色）:**
```json
{ "field": 0, "startCol": 1, "colors": [{ "r": 255, "g": 0, "b": 0 }, { "r": 0, "g": 255, "b": 0 }] }
```

### POST /api/light/block

ブロック単位で色を設定する（AC コマンド）。

**リクエスト:**
```json
{ "progNo": 1, "blockNo": 1, "color": { "r": 255, "g": 0, "b": 0 } }
```

### POST /api/light/block-sector

ブロックセクタ単位で色を設定する（AE コマンド）。

**リクエスト:**
```json
{ "progNo": 1, "blockNo": 1, "color": { "r": 255, "g": 0, "b": 0 } }
```

### POST /api/light/sequence

ハードウェアシーケンスを再生する（A1 コマンド）。

**リクエスト:**
```json
{ "frameNo": 0 }
```

### POST /api/light/rx-channel

受信チャネルを設定する（A6 コマンド）。

**リクエスト:**
```json
{ "field": 0, "startRow": 1, "len": 3, "channel": 2 }
```

---

## 4. Effect（エフェクト制御）

エフェクトはバックグラウンドで継続実行される。新しいエフェクトを開始すると前のエフェクトは自動停止する。

### POST /api/effect/start

エフェクトを開始する。

**リクエスト:**
```json
{
  "type": "Flash",
  "color": { "r": 255, "g": 0, "b": 0 },
  "field": 0,
  "cycleDurationMs": 1000,
  "flashIntervalMs": 500,
  "fadeSteps": 20,
  "continuous": true
}
```

| パラメータ | デフォルト | 説明 |
|-----------|-----------|------|
| type | (必須) | `Flash` `FadeIn` `FadeOut` `Breathing` `SevenColor` |
| color | (必須) | エフェクトの基本色 |
| field | `0` | 対象フィールド |
| cycleDurationMs | `1000` | 1サイクルの時間(ms) |
| flashIntervalMs | - | Flash時のON/OFF間隔(ms) |
| fadeSteps | `20` | Fade補間ステップ数 |
| continuous | `true` | 連続再生するか |

### POST /api/effect/stop

実行中のエフェクトを停止する。リクエストボディ不要。

### GET /api/effect/status

エフェクトの実行状態を取得する。

**レスポンス:**
```json
{
  "success": true,
  "data": {
    "isRunning": true,
    "effectType": "Flash",
    "color": { "r": 255, "g": 0, "b": 0 }
  }
}
```

---

## 5. Sequence（シーケンス管理・再生）

シーケンスは時間ベースのコマンドステップを順次実行する。JSON ファイルとして永続化される。

### GET /api/sequence

保存済みシーケンス名の一覧を取得する。

**レスポンス:**
```json
{
  "success": true,
  "data": { "names": ["countdown", "rainbow", "alert"] }
}
```

### GET /api/sequence/{name}

指定名のシーケンスを取得する。

**レスポンス:**
```json
{
  "success": true,
  "data": {
    "name": "countdown",
    "steps": [
      { "timeOffsetMs": 0, "commandType": "Color", "r": 255, "g": 0, "b": 0, "field": 0 },
      { "timeOffsetMs": 1000, "commandType": "Color", "r": 255, "g": 255, "b": 0, "field": 0 },
      { "timeOffsetMs": 2000, "commandType": "Color", "r": 0, "g": 255, "b": 0, "field": 0 },
      { "timeOffsetMs": 3000, "commandType": "Off" }
    ]
  }
}
```

### POST /api/sequence

シーケンスを保存（作成・上書き）する。

**リクエスト:**
```json
{
  "name": "countdown",
  "steps": [
    { "timeOffsetMs": 0, "commandType": "Color", "r": 255, "g": 0, "b": 0 },
    { "timeOffsetMs": 1000, "commandType": "Off" },
    {
      "timeOffsetMs": 2000,
      "commandType": "Effect",
      "effectType": "Flash",
      "r": 255, "g": 255, "b": 255,
      "effectCycleDurationMs": 500
    },
    { "timeOffsetMs": 5000, "commandType": "EffectStop" }
  ]
}
```

**SequenceStep フィールド:**

| フィールド | 型 | 説明 |
|-----------|---|------|
| timeOffsetMs | int | シーケンス開始からの経過時間(ms) |
| commandType | string | `Color` `Off` `Effect` `EffectStop` |
| r, g, b | byte | 色 (Color/Effect 時) |
| field | byte | 対象フィールド |
| retransmitCount | int? | 再送回数 (null=グローバル設定) |
| effectType | string? | `Flash` `FadeIn` `FadeOut` `Breathing` `SevenColor` (Effect 時) |
| effectCycleDurationMs | int? | エフェクト周期(ms) |
| fadeSteps | int? | Fade補間ステップ数 |

### DELETE /api/sequence/{name}

シーケンスを削除する。

### POST /api/sequence/play

シーケンスの再生を開始する。バックグラウンドで実行される。

**リクエスト:**
```json
{ "name": "countdown" }
```

### POST /api/sequence/stop

再生中のシーケンスを停止する。リクエストボディ不要。

### GET /api/sequence/play/status

再生状態を取得する。

**レスポンス:**
```json
{
  "success": true,
  "data": {
    "isPlaying": true,
    "sequenceName": "countdown"
  }
}
```

---

## エンドポイント一覧

| カテゴリ | メソッド | エンドポイント | 概要 |
|---------|---------|--------------|------|
| Transport | GET | /api/transport/scan | COM ポートスキャン |
| Transport | POST | /api/transport/connect | 接続 |
| Transport | POST | /api/transport/disconnect | 切断 |
| Transport | GET | /api/transport/status | 状態取得 |
| Transport | POST | /api/transport/keepalive | キープアライブ送信 |
| Transmitter | POST | /api/transmitter/init | 初期化 (ch + pwr) |
| Transmitter | POST | /api/transmitter/set-channel | チャネル設定 |
| Transmitter | POST | /api/transmitter/set-power | 電力設定 |
| Light | POST | /api/light/global | 全体色設定 (A2) |
| Light | POST | /api/light/off | 全消灯 |
| Light | POST | /api/light/points | 座標指定 (A0) |
| Light | POST | /api/light/rows | 複数行同色 (AA) |
| Light | POST | /api/light/rows/each | 行制御 (A3) |
| Light | POST | /api/light/cols | 複数列同色 (A8) |
| Light | POST | /api/light/cols/each | 列制御 (A4) |
| Light | POST | /api/light/block | ブロック (AC) |
| Light | POST | /api/light/block-sector | ブロックセクタ (AE) |
| Light | POST | /api/light/sequence | HWシーケンス再生 (A1) |
| Light | POST | /api/light/rx-channel | 受信ch設定 (A6) |
| Effect | POST | /api/effect/start | エフェクト開始 |
| Effect | POST | /api/effect/stop | エフェクト停止 |
| Effect | GET | /api/effect/status | エフェクト状態 |
| Sequence | GET | /api/sequence | 一覧取得 |
| Sequence | GET | /api/sequence/{name} | 取得 |
| Sequence | POST | /api/sequence | 保存 |
| Sequence | DELETE | /api/sequence/{name} | 削除 |
| Sequence | POST | /api/sequence/play | 再生開始 |
| Sequence | POST | /api/sequence/stop | 再生停止 |
| Sequence | GET | /api/sequence/play/status | 再生状態 |

## 利用手順

1. `GET /api/transport/scan` で COM ポートを確認
2. `POST /api/transport/connect` で接続
3. `POST /api/transmitter/init` で送信機を初期化
4. 照明制御 / エフェクト / シーケンスを利用
5. `POST /api/transport/disconnect` で切断
