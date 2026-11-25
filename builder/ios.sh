#!/usr/bin/env bash
# Builds a project's iPhone app for the simulator, on a Mac with Xcode. The Mac build job
# (.github/workflows/ios-build.yml) runs it on the source a desktop sent; nothing here knows where the source
# came from or where the app goes.
#
#   ios.sh <source folder> <output folder> [the app's folder inside the source, "." when not said]
#
# It leaves <output>/app.tar.gz, one `.app` built for the arm64 simulator in the Debug configuration: the
# build a developer runs from Xcode, which loads its JavaScript or Dart from the desktop's own server.
#
# Everything it writes goes to its caller's output. The job sends that to the team's own storage and never to
# the job's log, which anyone can read.
set -euo pipefail

SOURCE=$(cd "$1" && pwd)
OUT=$(mkdir -p "$2" && cd "$2" && pwd)
APP_DIR="$SOURCE/${3:-.}"
[ -d "$APP_DIR" ] || { echo "The source has no folder ${3:-.}."; exit 2; }
cd "$APP_DIR"

export CI=1 EXPO_NO_TELEMETRY=1 RCT_NO_LAUNCH_PACKAGER=1 SENTRY_DISABLE_AUTO_UPLOAD=true COCOAPODS_DISABLE_STATS=1
export LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8

# The newest released Xcode on the Mac, unless the caller chose one.
if [ -z "${DEVELOPER_DIR:-}" ]; then
  newest=$(ls -d /Applications/Xcode_[0-9]*.app 2>/dev/null | grep -v -i beta | sort -V | tail -1 || true)
  [ -n "$newest" ] && export DEVELOPER_DIR="$newest/Contents/Developer"
fi
echo "Xcode: $(xcodebuild -version | tr '\n' ' ')"

has_package() { [ -f package.json ] && node -e "const p=require('./package.json');process.exit(({...p.dependencies,...p.devDependencies})['$1']?0:1)"; }

kind=${WORKSTREAM_APP_KIND:-}
if [ -z "$kind" ]; then
  if has_package expo; then kind=expo
  elif has_package react-native; then kind=react-native
  elif [ -f pubspec.yaml ]; then kind=flutter
  elif ls ./*.xcworkspace ./*.xcodeproj >/dev/null 2>&1; then kind=xcode
  else echo "No iPhone app found here: no Expo or React Native package, no pubspec.yaml and no Xcode project."; exit 2
  fi
fi
echo "Kind: $kind"

install_packages() {
  if [ -f bun.lock ] || [ -f bun.lockb ]; then
    command -v bun >/dev/null || npm install -g bun
    bun install --frozen-lockfile
  elif [ -f pnpm-lock.yaml ]; then corepack enable && pnpm install --frozen-lockfile
  elif [ -f yarn.lock ]; then corepack enable && yarn install --immutable || yarn install --frozen-lockfile
  elif [ -f package-lock.json ]; then npm ci --no-audit --no-fund
  else npm install --no-audit --no-fund
  fi
}

# The app's own scheme in a workspace or project: the one named after the project, not a dependency's.
build_xcode() { # <folder holding the workspace or project>
  local folder=$1 container flag scheme
  container=$(ls -d "$folder"/*.xcworkspace 2>/dev/null | grep -v '/project.xcworkspace$' | head -1 || true)
  flag=-workspace
  if [ -z "$container" ]; then container=$(ls -d "$folder"/*.xcodeproj | head -1); flag=-project; fi
  scheme=${WORKSTREAM_APP_SCHEME:-$(basename "${container%.*}")}
  echo "Building $scheme from $(basename "$container")"
  xcodebuild "$flag" "$container" -scheme "$scheme" -configuration Debug -sdk iphonesimulator \
    -destination 'generic/platform=iOS Simulator' -derivedDataPath "$OUT/derived" \
    ARCHS=arm64 ONLY_ACTIVE_ARCH=NO COMPILER_INDEX_STORE_ENABLE=NO build
  products="$OUT/derived/Build/Products/Debug-iphonesimulator"
}

case "$kind" in
  expo)
    install_packages
    npx expo prebuild --platform ios
    build_xcode ios
    ;;
  react-native)
    install_packages
    (cd ios && { [ ! -f Gemfile ] || bundle install; } && pod install)
    build_xcode ios
    ;;
  flutter)
    flutter pub get
    flutter build ios --simulator --debug
    products=build/ios/iphonesimulator
    ;;
  xcode)
    build_xcode .
    ;;
  *) echo "Unknown kind of app: $kind"; exit 2 ;;
esac

app=$(ls -d "$products"/*.app | head -1)
[ -d "$app" ] || { echo "The build finished without an app."; exit 1; }
echo "Built $(basename "$app"), $(du -sh "$app" | cut -f1), for $(lipo -archs "$app/$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app/Info.plist")")"
tar -czf "$OUT/app.tar.gz" -C "$(dirname "$app")" "$(basename "$app")"
/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app/Info.plist" > "$OUT/bundle-id.txt"
echo "Done."
