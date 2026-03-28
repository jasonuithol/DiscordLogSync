rm -rf staging release

VERSION=$(grep version_number ThunderstoreAssets/manifest.json | grep -o '[0-9]*\.[0-9]*\.[0-9]*')
MODNAME=$(basename "$PWD")
TARGET=release/tarbaby-${MODNAME}-${VERSION}.zip

mkdir -p staging/plugins release
cp ThunderstoreAssets/* staging/
cp bin/Release/netstandard2.1/*.dll staging/plugins/
cd staging && zip -r ../${TARGET} . && cd ..

echo Created ${TARGET}
