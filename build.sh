#!/bin/sh
# Full build + package (Linux box or Git Bash). Requires dotnet 8 SDK; mingw for the dummy.
set -e
cd "$(dirname "$0")"
V=0.3.6
dotnet test tests/Vanta.Tests
[ -f tools/dummy/vanta_dummy.exe ] || sh tools/dummy/build.sh
dotnet run --project src/Vanta.Cli -c Release -- validate games
dotnet run --project src/Vanta.Cli -c Release -- index games
rm -rf dist/Vanta && mkdir -p dist/Vanta/selftest dist/Vanta/docs
dotnet publish src/Vanta.App -c Release -o dist/pub
cp dist/pub/Vanta.exe dist/Vanta/
cp -r games schema dist/Vanta/
cp tools/dummy/vanta_dummy.exe tools/dummy/selftest.game.json dist/Vanta/selftest/
cp README.txt CHANGELOG.md LEESMIJ.txt LICENSE dist/Vanta/
cp README-DEV.md docs/BRANDING.md docs/privacy.md dist/Vanta/docs/
cd dist && rm -f Vanta-v$V.zip
if command -v zip >/dev/null; then zip -r -9 Vanta-v$V.zip Vanta
else python3 -c "import shutil; shutil.make_archive('Vanta-v$V','zip','.','Vanta')"; fi
sha256sum Vanta-v$V.zip > Vanta-v$V.zip.sha256
cat Vanta-v$V.zip.sha256
