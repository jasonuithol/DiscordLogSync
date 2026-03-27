rm -rf staging release

VERSION=$(grep version_number ThunderstoreAssets/manifest.json | grep -o '[0-9]*\.[0-9]*\.[0-9]*')
MODNAME=$(basename "$PWD")
TARGET=release/tarbaby-${MODNAME}-${VERSION}.zip

mkdir -p staging/plugins staging/config release
cp ThunderstoreAssets/icon.png ThunderstoreAssets/README.md ThunderstoreAssets/manifest.json staging/
cp bin/Release/netstandard2.1/*.dll staging/plugins/
cp *.cfg staging/config/
cd staging && zip -r ../${TARGET} . && cd ..

echo Created ${TARGET}
