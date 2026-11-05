# Puppis

日本のテレビ放送のチューナーを動かす driver。\
チューナーを占有し、B-CAS カードでスクランブルを解いて録画ファイルを書く。\
録画や視聴を頼む側には Unix ドメインソケットの口で答え、カードやチューナーを家の中の別の機械に貸すこともできる。

口の取り決めは `Puppis.Contracts` として Puppis が持ち、nuget.org の package で出す。\
この口の使い手は今のところ録画システムの [Carina](https://github.com/NewWorldOrg/Carina) だけ。\
Carina は番組表・予約・配信の API を、[Vela](https://github.com/NewWorldOrg/Vela) は画面を受け持つ。\
動作を確かめた版の組は [Pyxis](https://github.com/NewWorldOrg/Pyxis) が固定して配る。

## 構成

| プロジェクト | 役割 |
| --- | --- |
| `Puppis.Driver` | 特権で動く。チューナーを占有し、スクランブルを解いて TS を録画ファイルに書く。app の求めには Unix ドメインソケットで答える |
| `Puppis.Contracts` | driver の口の取り決め。メッセージの形と口の名前。nuget.org の package `Puppis.Contracts` として出し、使う側はそれを参照する |

driver は app のための TCP ポートを開かない。\
app と別のプロセスなので、app を入れ替えても進行中の録画は止まらない。\
録画ファイルを書くのは driver だけで、app は読むだけ。

```
src/        Puppis.Driver と Puppis.Contracts
tests/      試験。src の 2 つに対応するものと、規則を読む Puppis.Architecture.Tests
docker/     開発用の driver の設定と、イメージに同梱するライセンスの検査
```

## 必要なもの

Docker だけで動く。\
チューナーも B-CAS カードも要らない。\
実機の代わりに合成のチューナーが動くので、選局からセッションまでひととおり試せる。

## セットアップ

```bash
task up            # 合成のチューナーの driver
task probe:driver  # ヘルスチェック
```

Task を使わない場合は次のとおり。

```bash
docker compose up -d
docker compose exec driver dotnet /driver/bin/Puppis.Driver/debug/Puppis.Driver.dll --probe
```

開発用のコンテナはソースから driver を組んで起動し、ソケットを `/run/carina/driver.sock` に開く。\
`docker compose ps` の health は `--probe` の結果で、次のときに unhealthy になる。

- ソケットが応答しない
- 停止に向けて、録画が終わるのを待っている
- 使えるチューナーが 1 本も無い

health は状態を表示するだけで、unhealthy になっても何も再起動しない。\
ビルド・試験・lint のコマンドは `CLAUDE.md` にある。

## 設定

driver は `PUPPIS_DRIVER_CONFIG` が指す JSON ファイルを 1 つだけ読む。\
古い名前の `CARINA_DRIVER_CONFIG` も読み、両方あるときは `PUPPIS_DRIVER_CONFIG` を使う。\
既定のパスは無い。\
読めない項目や知らないキーがあると、問題のある項目を表示して起動を止める。\
開発用の設定は `docker/driver.development.json` にある。

| キー | 用途 |
| --- | --- |
| `socketPath` | app とつなぐ Unix ドメインソケット。`/run/` の下に置く。既定は `/run/carina/driver.sock`。既定は使う側の app の既定に合わせてある |
| `socketGroupId` | ソケットの所有グループの id。既定は 10001。app を動かすグループと同じ値にする |
| `outputRoots` | 録画の保存先。`name` と絶対パスの `path` の組を 1 つ以上書く |
| `tuner.backend` | `dvb`(Linux DVB のチューナー)、`usb`(USB のチューナー)か `fake`(合成のチューナー)。既定値は無い |
| `devices` | チューナーの一覧。1 つ以上書き、うち 1 つ以上を `enabled` にする |
| `shutdownGraceHours` | 録画中に止められたとき、録画が終わるのを待つ上限の時間。1 から 168 で、既定は 6 |
| `name` | 画面でこの機械を呼ぶ名前。1 から 32 文字で、前後に空白を置かない。書かなければ画面は「この機械」と出す。ホスト名は自動では取らない |

`devices` の各要素には `id`・`kind`・`enabled` を書く。\
`kind` は `terrestrial` か `satellite`、`enabled` の既定は `true`。\
`tuner.backend` が `dvb` か `usb` なら、装置のパス `devicePath`(`/dev/` の下)も要る。\
衛星のチューナーで LNB に給電するなら、`lnbPower` を `true` にする。\
セッションの上限時間や demux のバッファなど、ほかのキーは既定のままで動く。\
`liveSessionMinutes` は最後に要求されてからセッションを保つ時間で、視聴できる長さではない。\
視聴中は app がその都度延長するので、視聴が途中で切れることはない。

`tuner.backend` が `dvb` のとき、driver はホストの pcscd 経由で B-CAS カードを使い、スクランブルを解いて録画する。\
driver のコンテナには、ホストの `/run/pcscd` をディレクトリごと渡す。\
pcscd は起動し直すたびにソケットを作り直すので、ソケットのファイルだけを渡すと古いソケットを指したままになる。\
カードが応答しないときはスクランブルされたまま録画し、その量を品質の数値として記録する。

`tuner.backend` が `usb` のとき、driver は USB の地上デジタルチューナー CECH-ZD1(054C:04B2)を usbdevfs で直に動かす。\
`devicePath` は `/dev/bus/usb/` の下のノードで、driver を動かすユーザーが読み書きできるようにしておく。\
チューナーの id はつないだポートから付き、driver は開くたびにそのポートの今のノードを探すので、同じポートに挿し直しても設定はそのままでよい。\
`devicePath` は、id がどのポートにも当たらないときにだけ使う。\
電源が入ってから一度もほかの機器に動かされていない状態からしか動かさないので、そうでないと言われたら挿し直す。\
このチューナーの出力は、`cardLending.borrow` を書かなければスクランブルを解かずに録画する。

### カードの貸し借り

B-CAS カードが 1 枚しかない家で、カードの無い機械の driver も放送を解けるようにする。\
カードを挿した機械の driver が貸し手になり、ECM を受けてスクランブル鍵を返す口を専用の TCP ポートに開く。\
借り手の driver は、チューナーを開くたびに貸し手へつなぎ、その接続の上で鍵を借りて自分で解く。\
カードへの生のコマンドは網に出さない。\
貸し手の口は既定では開かず、`cardLending.lend` を書いたときだけ開く。

| キー | 用途 |
| --- | --- |
| `cardLending.lend.address` | 口を開くアドレス。`0.0.0.0`・`::` か家の中のアドレス(ループバック、10/8、172.16/12、192.168/16、169.254/16、fc00::/7、fe80::/10) |
| `cardLending.lend.port` | 口のポート。1024 から 65535 で、既定は 40772 |
| `cardLending.lend.hostNames` | 借り手が名乗る貸し手の名前。1 から 8 個 |
| `cardLending.lend.hopLimit` | 返すパケットの TTL(IPv6 はホップ数)。1 から 8 で、既定は 3 |
| `cardLending.lend.roundTripMilliseconds` | 往復時間の上限。1 から 30 で、既定は 5 |
| `cardLending.lend.leases` | 同時に貸す数。1 から 16 で、既定は 4 |
| `cardLending.lend.keyFile` | 貸し手と借り手で同じ鍵を置くファイル |
| `cardLending.borrow.address` | 貸し手のアドレス。家の中のアドレスだけ |
| `cardLending.borrow.port` | 貸し手のポート。既定は 40772 |
| `cardLending.borrow.hostName` | 貸し手の名前。貸し手の `hostNames` のどれか |
| `cardLending.borrow.hopLimit` | 送るパケットの TTL。既定は 3 |
| `cardLending.borrow.keyFile` | 鍵のファイル |

1 つの driver は貸すか借りるかのどちらかで、両方は書けない。\
鍵は 32 文字以上の文字列で、`openssl rand -base64 48` などで作る。\
鍵のファイルを所有者とグループ以外が読めると、driver は起動しない。

鍵そのものは網に流さない。\
貸し手の nonce・借り手の nonce・名乗った名前から両方が同じ値を導き、互いに鍵を持つことを確かめる。\
貸した鍵は貸し出しごとに導いた鍵の AES-GCM で封をして返し、借り手は封の合わないものと一度受け取ったものを捨てる。\
貸し出しは接続 1 本に結び付き、接続が閉じるとカードの接続も返す。\
ECM と鍵の中身はログに書かない。

貸す前に、借り手との往復時間を 3 回測り、最も速い回が上限を超えたら貸さない。\
3 回とも、貸し手が出した nonce に、それまでのやり取りと何回目かに結び付けた鍵の証明で答えたものだけを測る。\
家の外からルーターの VPN・`ssh -R`・socat・ポートの公開・リバースプロキシのような中継を通っても、遠い借り手は通らない。\
鍵を持たない中継は代わりに答えられず、鍵を持つ側までの往復は経路より短くならないため。\
最も速い回で比べるので、LAN の揺れで断ることは少ない。

まだ借りていない接続は全部で 8 本まで、1 つのアドレスから 3 本までとし、2 秒のうちに借りなければ閉じる。\
IPv6 は /64 を 1 つのアドレスと数える。\
同じアドレスの 3 本か全体の 8 本がふさがっているときに新しい接続が来たら、そこでいちばん長く待っている接続を閉じて入れる。\
鍵の証明で 1 度でも答えた接続は閉じない。\
借りている接続はこの数に入らないので、鍵を持たない機器が接続を開き続けても貸し出しは止まらない。

貸し手は、次のものに答えない。

- 家の中のアドレス以外からの接続。CGNAT の 100.64/10 も外とみなし、読まずに閉じる
- `hostNames` に無い名前の要求
- `Forwarded`・`X-Forwarded-For`・`X-Real-IP`・`Via`・`CF-Connecting-IP` などプロキシを通った印のある要求と、`Origin` のあるブラウザからの要求

この口を公開の入口に載せない。\
TTL の既定の 3 は、コンテナのネットワークの中の貸し手と NAT の内側の借り手の組で、両向きとも届く最小の値。\
どちらもホストのネットワークで同じ LAN にいるなら 1 で足りる。

## driver の止め方

録画中に停止や再起動を指示すると、driver はその録画が終わってから止まる。\
待つのは `shutdownGraceHours` までで、それを過ぎると録画を打ち切る。\
`--shutdown-budget` を付けて起動すると、設定から待つ上限の秒数を出して終わる。

実行環境では、次の 2 点を守る。

- `stop_grace_period` を、driver が申告する秒数より長くする。短いと後処理の途中で SIGKILL される
- 再起動ポリシーに `on-failure` を使わない。要求による停止は終了コード 0、それ以外は 70 で終わる

driver は呼び出し元を認証しない。\
アクセスを制限するのは Unix ドメインソケットのパーミッションと所有グループだけ。\
TCP のポートを開くのは、`cardLending.lend` を書いたときのカードの貸し借りの口だけ。

```bash
task logs:driver
task restart:driver   # コード変更の反映
```

## イメージ

`Dockerfile` は driver を Native AOT で 1 つのバイナリに組み、libpcsclite1 だけを足した `runtime-deps` のイメージに置く。\
.NET のランタイムも ffmpeg も入っていない。\
entrypoint は driver のバイナリそのもので、root で動く。\
設定は `PUPPIS_DRIVER_CONFIG` で渡し、pcscd のディレクトリ・録画の置き場・ソケットのディレクトリをホストから渡す。

```bash
docker run --rm \
  -e PUPPIS_DRIVER_CONFIG=/etc/puppis/driver.json \
  -v ./driver.json:/etc/puppis/driver.json:ro \
  -v /run/pcscd:/run/pcscd:ro \
  -v driver-run:/run/carina \
  -v /srv/recordings:/srv/recordings \
  --device /dev/dvb/adapter0 \
  ghcr.io/newworldorg/puppis:v0.1.0
```

`/run/carina` は app のコンテナと同じボリュームにし、app の `CARINA_DRIVER_SOCKET` を driver の `socketPath` に合わせる。

### タグ

`master` にマージされるたびに、CI がイメージを `ghcr.io/newworldorg/puppis` に公開する。\
対応する CPU は amd64 だけ。

| タグ | 指すもの |
| --- | --- |
| `sha-<commit>` | そのコミットのイメージ。マージのたびに出る |
| `vX.Y.Z` | その版のコミットの `sha-<commit>` と同じイメージ |
| `latest` | prerelease(`v0.2.0-rc.1` のように `-` を含むもの)を除いた最も新しい版 |

一度公開したタグは上書きしない(`latest` を除く)。\
`vX.Y.Z` のタグを push すると、CI はイメージを作り直さずにそのコミットの `sha-<commit>` へ版のタグを足し、GitHub Release を作る。

## 取り決めの package

`Puppis.Contracts` は nuget.org の package で、Carina の app は版を固定して参照する。\
版は `Directory.Build.props` の `Version` で、イメージと同じ番号。\
`vX.Y.Z` のタグを push すると、CI が `dotnet pack` して nuget.org に出す。\
push は secret を持たず、nuget.org の Trusted Publishing の policy(Repository Owner NewWorldOrg・Repository Puppis・Workflow File release.yml)で行う。

driver 本体も `Puppis.Driver` の package で出す。\
使う側の統合試験が、driver を自分のプロセスの中に立てて app との組を動かすためのもので、運用で使うのはイメージのほう。\
版は `Puppis.Contracts` と同じ番号で、同じ番号の `Puppis.Contracts` に依存する。

取り決めの変更は足すだけ。\
口やイベントを消さず、名前を変えない。\
新しい口を足したら、driver は挨拶で返すできることの一覧にその名前を足し、app は一覧を見てから頼む。\
`DriverProtocol.Version` は、一覧で表せない変更(メッセージの形の変更)だけで上げる。\
古い driver と新しい app の組も、新しい driver と古い app の組も、正常な組。

### 版の上げ方

1. `Directory.Build.props` の `Version` を上げる。driver のイメージと package は同じ番号で出る
2. `master` にマージし、`sha-<commit>` のイメージが出るのを待つ
3. `vX.Y.Z` のタグを push する。CI が版のタグ・GitHub Release・package を出す
4. Carina の `Directory.Packages.props` で `Puppis.Contracts` と `Puppis.Driver` の版を上げる

タグが `Directory.Build.props` の版と違うと、CI は止まる。\
リリースノートには、足した口とできることの一覧の名前を書く。

## ライセンス

AGPL-3.0-only。著作権者は NewWorldOrg。\
詳細は `LICENSE` にある。\
イメージに同梱した他のソフトウェアとそのライセンスは `THIRD-PARTY-NOTICES.md` に載せ、イメージの中では `/usr/share/doc/puppis/` にある。
