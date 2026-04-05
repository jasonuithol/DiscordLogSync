TARGET="${HOME}/.steam/steam/steamapps/common/Valheim dedicated server"

DLL_FILES=bin/Release/netstandard2.1/*.dll
CFG_FILES=*.cfg

cp $DLL_FILES "${TARGET}"/BepInEx/plugins/
cp $CFG_FILES "${TARGET}"/BepInEx/config/

echo Files deployed to Valheim Server BepInEx plugin folder: $DLL_FILES
echo Files deployed to Valheim Server BepInEx config folder: $CFG_FILES



