# Third-party notices

Puppis is licensed under AGPL-3.0-only (`LICENSE`). This file names what the
container image carries beside Puppis. The image keeps this file, `LICENSE` and
the license texts named below under `/usr/share/doc/puppis/`.

Building the image fails when the NuGet and Ubuntu tables below differ from what
the image carries.

## .NET

The driver is compiled by native AOT, which links the .NET runtime packs into its
binary; the runtime and the compiler are MIT licensed, and the license and notices
of each pack are under `/usr/share/doc/puppis/dotnet/<pack>/<version>/`. The image
carries no shared .NET runtime.

## NuGet packages

The driver references no NuGet package. The index of what it carries is
`/usr/share/doc/puppis/nuget/driver.tsv`, and the license and notices of a package
would be under `/usr/share/doc/puppis/nuget/<package>/<version>/`. A package added
to the driver needs a row here, in a table of `Package`, `License` and
`Carried by` as the check reads it.

## Ubuntu packages added to the base image

The license of each package is `/usr/share/doc/<package>/copyright`.

| Package | Source package | License |
| --- | --- | --- |
| `libpcsclite1` | `pcsc-lite` | BSD-3-Clause |

The driver loads `libpcsclite` at run time to reach the card reader through the
host's pcscd. Nothing else is installed over the base image.

## Packages of the base image

The base image `mcr.microsoft.com/dotnet/runtime-deps` is Ubuntu 24.04. The
license of each of its packages is `/usr/share/doc/<package>/copyright`, and
Ubuntu publishes the source of every version at
`https://launchpad.net/ubuntu/+source/<source package>/<version>`, under the name
and version that `dpkg-query -W -f '${source:Package} ${source:Version}\n'`
reports in the image.
