# Puppis

日本のテレビ放送のチューナーを制御する driver です。\
チューナーを占有して選局し、B-CAS カードでスクランブルを解除して録画ファイルを書きます。\
録画や視聴の要求は Unix ドメインソケットで受け付け、カードを LAN 内の別の機械に貸すこともできます。

driver を使う側との通信プロトコルは `Puppis.Contracts` として nuget.org に公開しています。\
今の利用者は録画システムの [Carina](https://github.com/NewWorldOrg/Carina) で、本番の構成は [Pyxis](https://github.com/NewWorldOrg/Pyxis) にあります。

## 動かす

イメージは `ghcr.io/newworldorg/puppis` にあり、`v0.1.1` のようなバージョンのタグと `latest` が使えます。\
driver は root で動き、設定ファイルのパスを `PUPPIS_DRIVER_CONFIG` で渡します。

```bash
docker run -d --privileged \
  -e PUPPIS_DRIVER_CONFIG=/etc/puppis/driver.json \
  -v ./driver.json:/etc/puppis/driver.json:ro \
  -v /run/pcscd:/run/pcscd:ro \
  -v driver-run:/run/carina \
  -v /srv/recordings:/srv/recordings \
  --device /dev/dvb/adapter0 \
  ghcr.io/newworldorg/puppis:v0.1.1
```

`/run/carina` は Carina の app と同じボリュームにし、`/run/pcscd` はホストの pcscd のディレクトリをそのまま渡します。

録画中に停止すると、driver は録画が終わるまで待ってから止まります。\
コンテナの `stop_grace_period` は `shutdownGraceHours` より長くし、再起動ポリシーに `on-failure` は使わないでください。

## 設定

`driver.json` の主なキーです。

| キー | 内容 |
| --- | --- |
| `socketPath` | ソケットのパス。既定は `/run/carina/driver.sock` |
| `socketGroupId` | ソケットの所有グループ。既定は 10001。app を動かすグループに合わせます |
| `outputRoots` | 録画の保存先。`name` と `path` の組を 1 つ以上 |
| `tuner.backend` | `dvb`、`usb`(CECH-ZD1)、`fake`(合成チューナー)のいずれか。必須です |
| `devices` | チューナーの一覧。`id`・`kind`(`terrestrial` か `satellite`)・`devicePath`・`enabled` |
| `shutdownGraceHours` | 停止時に録画の終了を待つ上限。既定は 6 時間 |
| `name` | 画面に表示するこの機械の名前。省略すると「この機械」と表示します |

`tuner.backend` が `dvb` のときは、`devicePath` に `/dev/dvb/adapter0/frontend0` のように frontend までのパスを書きます。\
`usb` のときは `/dev/bus/usb/` の下のデバイスノードを書き、`id` は接続したポートから自動で決まります。

### カードの貸し借り

B-CAS カードを挿した機械の driver が貸し手になり、TCP ポートでスクランブル鍵の問い合わせに答えます。\
借り手の driver は選局のたびに貸し手へ接続し、自分で解除します。\
1 つの driver は貸し手か借り手のどちらかになります。

| キー | 内容 |
| --- | --- |
| `cardLending.lend.address` / `port` | 貸し手が待ち受けるアドレスとポート。既定のポートは 40772 |
| `cardLending.lend.hostNames` | 貸し手の名前。借り手はこのどれかを名乗ります |
| `cardLending.lend.keyFile` | 共通鍵のファイル |
| `cardLending.borrow.address` / `port` | 貸し手のアドレスとポート |
| `cardLending.borrow.hostName` | 貸し手の名前 |
| `cardLending.borrow.keyFile` | 共通鍵のファイル |

鍵は 32 文字以上の文字列で、`openssl rand -base64 48` などで作り、所有者だけが読めるファイルに置きます。\
貸し手は LAN 内のアドレスからの接続だけを受け付け、往復時間を測って遠い借り手を断ります。\
このポートをインターネットに公開しないでください。

## パッケージ

`Puppis.Contracts` と `Puppis.Driver` を nuget.org に公開しています。\
バージョンはイメージと同じです。\
`vX.Y.Z` のタグを push すると、CI がイメージにタグを付け、GitHub Release とパッケージを公開します。\
プロトコルの変更は追加だけで、古い driver と新しい app の組み合わせでも動きます。

## 開発

Docker があれば動きます。チューナーもカードも不要で、合成チューナーで一通り試せます。

```bash
task up            # 合成チューナーの driver
task probe:driver  # ヘルスチェック
```

ビルド、テスト、lint のコマンドは `CLAUDE.md` にあります。

## ライセンス

AGPL-3.0-only です。著作権者は NewWorldOrg で、全文は `LICENSE` にあります。\
イメージに同梱したソフトウェアのライセンスは `THIRD-PARTY-NOTICES.md` と、イメージ内の `/usr/share/doc/puppis/` にあります。
