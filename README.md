# Puppis

日本のテレビ放送のチューナーを制御する driver です。\
チューナーを占有して選局し、B-CAS カードでスクランブルを解除して録画ファイルを書きます。\
録画や視聴の要求は Unix ドメインソケットで受け付け、カードを LAN 内の別の機械に貸すこともできます。

driver を使う側との通信プロトコルは `Puppis.Contracts` として nuget.org に公開しています。\
今の利用者は録画システムの [Carina](https://github.com/NewWorldOrg/Carina) で、本番の構成は [Pyxis](https://github.com/NewWorldOrg/Pyxis) にあります。

## 動かす

イメージは `ghcr.io/newworldorg/puppis` にあり、`v0.4.0` のようなバージョンのタグと `latest` が使えます。\
driver は root で動き、設定ファイルのパスを `PUPPIS_DRIVER_CONFIG` で渡します。

```bash
docker run -d --privileged \
  -e PUPPIS_DRIVER_CONFIG=/etc/puppis/driver.json \
  -e PUPPIS_DRIVER_STATE=/var/lib/puppis \
  -v ./driver.json:/etc/puppis/driver.json:ro \
  -v driver-state:/var/lib/puppis \
  -v /run/pcscd:/run/pcscd:ro \
  -v driver-run:/run/carina \
  -v /srv/recordings:/srv/recordings \
  --device /dev/dvb/adapter0 \
  ghcr.io/newworldorg/puppis:v0.4.0
```

`/run/carina` は Carina の app と同じボリュームにし、`/run/pcscd` はホストの pcscd のディレクトリをそのまま渡します。

録画中に停止すると、driver は録画が終わるまで待ってから止まります。\
コンテナの `stop_grace_period` は `shutdownGraceHours` より長くし、再起動ポリシーに `on-failure` は使わないでください。

## 設定

`driver.json` の主なキーと、環境変数です。

| キー | 内容 |
| --- | --- |
| `socketPath` | ソケットのパス。既定は `/run/carina/driver.sock` |
| `socketGroupId` | ソケットの所有グループ。既定は 10001。app を動かすグループに合わせます |
| `outputRoots` | 録画の保存先。`name` と `path` の組を 1 つ以上 |
| `tuner.backend` | `dvb`、`usb`(CECH-ZD1 と PIX-DT295)、`fake`(擬似チューナー)のいずれか。必須です |
| `devices` | チューナーの一覧。`id`・`kind`(`terrestrial` か `satellite`)・`devicePath`・`enabled` |
| `shutdownGraceHours` | 停止時に録画の終了を待つ上限。既定は 6 時間 |
| `name` | 画面に表示するこの機械の名前。省略すると「この機械」と表示します |
| `tuner.measureWhileIdle` | 使われていないチューナーで受信の強さを測るかどうか。既定は `true` |
| `PUPPIS_DRIVER_STATE`(環境変数) | 受信の記録を残すディレクトリ。省略すると記録はメモリにだけ持ちます |

`tuner.backend` が `dvb` のときは、`devicePath` に `/dev/dvb/adapter0/frontend0` のように frontend までのパスを書きます。\
`usb` のときは `/dev/bus/usb/` の下のデバイスノードを書き、`id` は接続したポートから自動で決まります。

PIX-DT295 は地上波と衛星の両方を受けられますが、1 台は `kind` に書いたどちらか一方だけに使います。\
1 台で両方を受け分ける使い方には対応していません。\
本体はアンテナに給電しないので、衛星では外から給電してください。\
衛星の受信は実機で確かめていません。\
カードの貸し借りを設定していなければ、本体のスロットに挿した B-CAS カードで解除します。\
本体のスロットのカードをほかの driver に貸すことはできません。\
同じ driver のほかのチューナーは本体のスロットのカードを使わないので、カードの貸し借りか自前のカードリーダーが要ります。\
driver が動いている間は、セッションが終わっても本体を開いて初期化したままにし、次のセッションは選局から始めます。\
driver を止めたとき、チューナーを無効にしたとき、本体が抜かれたときは、元の状態に戻して閉じます。\
電源を入れたあと別のソフトで使った PIX-DT295 は開けないので、抜いて挿し直してください。

### チューナーの選び方

同じ局を受けられるチューナーが複数空いているとき、driver はその局の受信の記録が良いチューナーを使います。\
記録はチューナーと局の組み合わせごとの直近 20 回分で、ロックできた割合を先に比べます。\
割合が同じなら C/N の中央値を比べ、差が 2 dB 未満なら同じとみなします。\
それでも決まらないときは、閉じている途中でないもの・開いたまま保っているもの・設定ファイルの順で決めます。\
空いているチューナーが無いときの動きは、記録があっても変わりません。

driver は、60 秒以上使われていないチューナーで、記録の無い局と記録が 30 日より古い局を 1 つずつ測ります。\
測るのは、これまでにセッションで受信できた局です。\
選局して C/N を数回読むだけで、映像は取り出さず、カードも使いません。\
測っている最中に要求が来たら、測るのをやめてそのチューナーを渡します。\
測らせたくないときは `tuner.measureWhileIdle` を `false` にします。

記録を再起動のあとも使うには、書き込めるディレクトリを `PUPPIS_DRIVER_STATE` で渡します。\
driver はそこに `reception.json` を書きます。\
指定が無いときと書き込めないときは、driver を止めると記録が消えます。\
今の記録は、ソケットの `GET /tuners/reception` で見られます。

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
