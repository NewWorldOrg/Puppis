# Puppis

A tuner driver for Japanese television broadcasting: a privileged process that
owns the tuner hardware, descrambles the transport stream with the B-CAS card,
writes recordings, lends the card and the tuners inside the household, and
answers whoever asks over a Unix domain socket. The contract of that socket is
Puppis's own, published as the `Puppis.Contracts` package. Its first client is
the recording system [Carina](https://github.com/NewWorldOrg/Carina); nothing
here references Carina.

Two properties decide most of what follows, and are the argument to answer before
relaxing any of it:

- **A recording in progress survives a deployment of the app.** The driver is a
  separate process on its own release stream; replacing the app must not touch it,
  and an old driver with a new app is the normal state.
- **Recording quality is observable.** Continuity errors are counted while the
  recording is being written, so a broken recording is found by searching for it
  rather than on playback.

`README.md` is for running this — setup, configuration, the image, the package.
This file is for changing it, and does not repeat what is there.

## Tech Stack

- .NET 10, xUnit. The driver is published by native AOT into one binary.
- The driver reaches the tuner through the Linux DVB API with P/Invoke, a USB tuner
  through usbdevfs, and the card reader through `libpcsclite`, loaded at run time.
  It answers the app over HTTP/1.1 on a Unix domain socket. It binds no TCP port for
  the app; the one TCP port it can open is card lending's, and only when the
  configuration asks.
- Build settings are central in `Directory.Build.props`, package versions in
  `Directory.Packages.props`. Neither is repeated per project. The version declared
  there is the version of the image and of the package alike.

## Architecture

Two projects, one repository.

| Project | What it holds |
| --- | --- |
| `src/Puppis.Driver` | privileged: tuning, descrambling, transport stream handling, sessions, recording files, card lending |
| `src/Puppis.Contracts` | the IPC contract — message records, enums, identifiers, endpoint paths — published as the NuGet package `Puppis.Contracts` |

Reference direction is one way, and `tests/Puppis.Architecture.Tests` holds it,
reading the project files rather than the compiled output so that a reference
declared but not yet used is still caught:

- `Puppis.Driver` references `Puppis.Contracts` and nothing else, and carries no
  package reference. Everything it needs is in the shared framework.
- `Puppis.Contracts` has neither project nor package references, because a package
  added there reaches every consumer transitively. It carries shared vocabulary
  only.
- The set of projects is itself asserted, so a new one is a deliberate act.

Puppis references nothing of Carina. Carina takes the contract from the package,
so the dependency runs from the app to the driver and never back.

## Invariants

Most of these are held by a rule test, and every rule test is paired with a
self-check that runs the same rule against a deliberately violating fixture — so
a green run means the rule holds, not that it inspected nothing. A rule marked
**trip wire** reads source text: it catches the ordinary way of breaking the
invariant, not every way, and its self-check states what walks past it.

- **Contract changes are additive only.** Removing or renaming an endpoint or an
  event breaks the "old driver, new app" combination. A new endpoint comes with a
  new name in the capabilities the greeting announces, and the app asks only for
  what the list names. `DriverProtocol.Version` moves only for a change the list
  cannot express — the shape of a message — and the tests of the contract hold the
  wire spelling of every member.
- **The driver's events are signals, not messages.** The event stream writes the
  name of the event as its only payload; a subscriber reads a signal as "re-read",
  never as what changed. One file serves the stream and a rule holds that the
  payload is the event's name.
- **The driver asks nobody on its socket who they are.** The gate is the socket's
  permissions and owning group; authentication would mean putting a secret in the
  privileged process. The only secret the driver may hold is the card-lending key,
  read from a file only its owner and group may read, and only when `cardLending`
  is configured. A rule holds that nothing in the driver names an authentication
  scheme, and the development compose clears every variable through which a web
  server would hand it a TCP port.
- **The card is lent only inside the household, and only as keys.** A driver that
  lends opens a port of its own, served by a server apart from the one on the
  socket, carrying the three lending requests and nothing else. It closes a
  connection from outside loopback, RFC 1918, ULA and link-local before reading
  it (the carriers' shared range is outside), does not answer a host it is not
  named, a request bearing a proxy's forwarding headers or a browser's `Origin`,
  and sends with a small TTL set on the listening socket so the handshake is
  limited too. Before lending it times three round trips with the borrower, each
  answered with a proof keyed to the transcript and to which round trip it is,
  and refuses when the quickest exceeds a cap that is always on (5 ms by default,
  30 at most). Connections that have not borrowed get seats of their own, few in
  all and fewer per address (an IPv6 /64 counting as one); a newcomer to a full
  address or a full door closes the one there that has waited longest, never one
  that has answered with the key, and each is closed two seconds after it opens
  unless it has borrowed. A lease is a challenge answered with a proof derived
  from the key, both nonces and the host, bound to one connection and given back
  when it closes; answers go back sealed with AES-GCM under a key derived per
  lease. No card command crosses the network, and no ECM or key reaches a log.
- **The process that writes a recording file is the only one that removes it.**
  The driver derives the file name from the recording id rather than being handed
  a path, and refuses a name that is not one of its own, a root it does not
  declare, a root that holds no file at all (which is what a lost mount looks
  like) and a recording a session is still writing. A file that no recording owns
  is removed only by root, path, size and last write handed together; the driver
  refuses a path that leaves the root or passes through a link, the file of a
  recording a session is writing, and a file whose size or last write is no longer
  the one it was handed, which it reads again just before it unlinks. A rule lists
  every way the source tree can change what is on disk, file and way together, so
  one more way in one file is seen; the recording writer and the atomic
  configuration file are the only places that open a file for writing, and the two
  erasers are the only places that delete under an output root.
- **The recording and the count come from the same chunk.** The session's read
  loop hands each chunk to the writer and then to the counter, because a second
  pipeline over the same stream puts its own back pressure on the read the
  recording depends on. Counting is always on; no setting turns it off. One rule
  names the files allowed to mention the counter; another lists the files carrying
  two or more marks of a transport-stream parser (the three type names, the sync
  byte, the 188 and 184 strides, the pid and continuity masks), and the recording
  writer is not among them. **Trip wire:** a loop that shows one mark or none — a
  stride written `4 + 180 + 4`, lower-case hex — walks past.
- **"Nothing counted this" and "this was counted and was clean" are different
  answers.** A driver that cannot count says so in its greeting rather than
  answering zero. A position rides on the continuity count and the scrambling
  count together, because the seconds it names carry both.
- **A total is what the stream should have carried, not what arrived.** The
  driver counts packets read and packets never seen separately — a recording that
  lost more than it received is ordinary in heavy rain.
- **Every count and the position beside it are read in one breath,** under a
  single lock, because a position read a moment later can place more losses than
  the count admits to.
- **Where a loss happened is kept as a second of the stream's own clock.** The
  programme clock reference is what the file is played back against: a byte
  offset only approximates it at a variable bit rate, and the wall clock keeps
  running while a recording is interrupted. The 33-bit wrap is followed through;
  a jump the broadcast spliced in is written down as a re-anchor, so the timeline
  only reads forwards. A packet that sets the discontinuity indicator is taken at
  its word; the size test behind it admits a hundred times the longest gap the
  standard allows between two clock readings, and exists only for breaks nobody
  declared.
- **The driver announces progress every thirty seconds** while anything is being
  recorded, so the app can write the counts into its ledger as the recording runs
  and one that dies part way through can be told from a perfect one.
- **The clock the positions are measured against is the one the recorded service
  carries, and the driver cannot yet know which that is.** Measurement runs on
  the whole multiplex and follows the first programme clock it hears, handing
  over only when that one goes silent. A PID filter that narrows the file to one
  service would leave the followed clock possibly belonging to a service the file
  no longer contains, so deciding how the recorded service's clock reaches the
  session is a precondition for adding that filter.
- **A session keeps the window it asked for, whoever else lets go of the tuner.**
  There is no method that moves a session's end earlier, and a rule holds that
  none came back and that nothing calls one. Bringing it back is a decision to be
  made rather than a change to slip in.
- **The time the driver spends getting to the first byte is the head the app's
  recorder was written against.** The waits for a lock, for the first byte and
  for a seat are read from the source and held to the seconds the recorder in
  Carina allows for them; moving one here is a change to make there too.
- **The version is declared once, in `Directory.Build.props`.** Every assembly
  carries it, the greeting answers with what the driver was built as, and a rule
  refuses a project that takes a version of its own.
- **The driver folds no text, so it keeps the invariant globalization tables**,
  and its tests go with it. The contract is read by an app that does fold, so the
  repository as a whole asks the runtime for its Unicode tables and a rule names
  the pair that does not.
- **Configuration is validated at startup** and the process stops with a message
  naming the offending setting. There is no hot reload. The one file the driver
  reads is the one `PUPPIS_DRIVER_CONFIG` names (`CARINA_DRIVER_CONFIG`, the former
  name, is read when the new one is not set), and the README's defaults are
  held equal to the code's.
- **A stop the driver was asked for exits 0; anything else exits 70.** Coming
  back is the supervisor's half of the deal, which is why `on-failure` is the one
  restart policy the driver must never be given.

## Conventions

- Value objects, identifiers included, are immutable — no property may have a
  setter.
- Time is taken from an injected `TimeProvider`, never from the ambient clock.
- Asynchronous methods end in `Async`.
- Declarations name their type. `var` is only for a right-hand side that is a
  `new`, a cast, or a projection into an anonymous type; a factory whose name
  carries the type is not enough. `.editorconfig` raises `IDE0008` to an error and
  `EnforceCodeStyleInBuild` makes the build enforce it.
- Nesting stays shallow: two deep, three only when nothing shallower reads as
  well, for conditional expressions and for blocks alike. A choice on two axes
  becomes a named mapping, a staged condition a named method that returns early
  or a `switch` expression.
- Comments earn their place or are absent. Code that needs a comment to be
  understood is rewritten instead.
- Warnings are errors. The build is the gate.
- Tests come first. There is no implementation without a test.
- Wire names — endpoint paths, JSON property names, enum spellings, the socket's
  default path, the environment variables the driver reads — are the contract with
  the app and do not change. Namespaces and project names are Puppis's own.

## Tests

There is one test project per production project, plus
`Puppis.Architecture.Tests` for the rules above. No test project may reference
another test project, and the architecture tests reference no production project.

Every test runs without a database, a device or a card: the synthetic tuner stands
in for hardware, and a driver under test is hosted in-process on a socket in a
temporary directory. CI runs each test project whole, with no filter, and fails on
zero tests because `dotnet test` exits 0 when nothing ran.

## Commands

Everything runs inside the `driver` container, which builds into a
container-local artifacts path so that building from the mounted tree never
writes over the assembly the running driver has open.

```bash
docker compose exec driver dotnet build
docker compose exec driver dotnet test
docker compose exec driver dotnet format --verify-no-changes
```

`Taskfile.yml` is the place for a repeatable operation — `task build`, `task
test`, `task lint`, `task format`, `task pack`, and the driver tasks the README
describes. Add a task rather than passing a longer command around by hand.

GitHub Actions runs, on push and pull request to `master`, the build with warnings
as errors, the format check and each test project. A second workflow builds the
image, starts it once on the synthetic tuners to see that it answers its own
`--probe`, and on `master` publishes it as `sha-<commit>`. A third, on a `v*` tag,
builds no image: it refuses a tag that is not the version `Directory.Build.props`
declares, gives the image that commit was already published under the release's
tag and `latest` when the release is the newest one that is not a prerelease,
creates the GitHub Release, and packs `Puppis.Contracts` and `Puppis.Driver` and
pushes them to nuget.org. The push holds no secret: `NuGet/login` turns the job's
OIDC token into a key that lives for an hour, under the Trusted Publishing policy
nuget.org keeps for this repository and `release.yml`. `.github/release-latest.sh`
makes the `latest` call and its `prove` runs on every pull request.

`THIRD-PARTY-NOTICES.md` is checked by the image build. `docker/notices/nuget.sh`
writes the license of every NuGet package and runtime pack the driver was
compiled with into the image, and `docker/notices/check.sh` fails the build when
the NuGet table or the table of Ubuntu packages added to the base image differs
from what the image holds, or when a package listed under a GPL license has no
source package beside it.

## Development environment

`compose.yml` brings up `driver` alone on the repository mounted at `/code`, with
`/run/carina` where the socket lives as a named volume an app container can share.
No tuner device is mapped: development runs against the synthetic tuner backend
named in `docker/driver.development.json`. Real hardware is attached by an
untracked compose override, which is also where the configuration for a real
machine belongs.

The `driver` service runs the driver as its own main process, so it receives
SIGTERM directly and `stop_grace_period` covers the recording linger. A tree that
does not compile costs one build attempt per cooldown rather than a restart loop.
