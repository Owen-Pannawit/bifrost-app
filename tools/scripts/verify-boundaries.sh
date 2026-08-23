#!/usr/bin/env bash
# Verifies the architectural boundaries that are load-bearing but invisible at review time.
# Intended for CI (TST-01 §8). Run from the repository root.
#
#   TC-615  no INTERNET permission in the shipped manifest      (NFR-306)
#   —       Bifrost.Core / .Drivers cannot see Android          (IMP-02 §2.1)
#   —       EmbedIO stays inside its adapter project            (ADR-009)
#
# A stray `using` compiles fine locally and only bites when the abstraction is needed, which is
# why these are checked mechanically rather than by discipline.

set -uo pipefail
fail=0

note() { printf '  %s\n' "$1"; }
pass() { printf '\033[32mPASS\033[0m  %s\n' "$1"; }
bad()  { printf '\033[31mFAIL\033[0m  %s\n' "$1"; fail=1; }

# ---------------------------------------------------------------- 1. Core must not see Android

hits=$(grep -rln --include='*.cs' -E '^\s*using\s+Android(\.|;)' \
         src/Bifrost.Core src/Bifrost.Drivers src/Bifrost.Server 2>/dev/null \
       | grep -v '/obj/' || true)

if [ -z "$hits" ]; then
  pass "Bifrost.Core / .Drivers / .Server contain no Android references"
else
  bad "Android reference leaked into a platform-free project:"
  echo "$hits" | while read -r f; do note "$f"; done
fi

for proj in Bifrost.Core Bifrost.Drivers Bifrost.Server Bifrost.Server.EmbedIO; do
  tfm=$(grep -o '<TargetFramework>[^<]*</TargetFramework>' "src/$proj/$proj.csproj" 2>/dev/null \
        | sed 's/<[^>]*>//g')
  case "$tfm" in
    *-android*) bad "$proj targets '$tfm' — must be platform-free so the compiler enforces the boundary" ;;
    "")         bad "$proj has no TargetFramework" ;;
    *)          pass "$proj targets $tfm (no Android surface)" ;;
  esac
done

# ---------------------------------------------------------------- 2. EmbedIO containment

hits=$(grep -rln --include='*.cs' -E '^\s*using\s+EmbedIO(\.|;)' src 2>/dev/null \
       | grep -v '/obj/' | grep -v 'src/Bifrost.Server.EmbedIO/' || true)

if [ -z "$hits" ]; then
  pass "EmbedIO is referenced only from Bifrost.Server.EmbedIO"
else
  bad "EmbedIO referenced outside its adapter — ADR-009's escape route is compromised:"
  echo "$hits" | while read -r f; do note "$f"; done
fi

hits=$(grep -rl 'Include="EmbedIO"' src --include='*.csproj' 2>/dev/null \
       | grep -v 'Bifrost.Server.EmbedIO.csproj' || true)
[ -z "$hits" ] \
  && pass "No project but the adapter takes a PackageReference on EmbedIO" \
  || bad "EmbedIO package referenced by: $hits"

# ---------------------------------------------------------------- 3. TC-615 — no INTERNET

# Parse uses-permission elements only. A naive text search matches explanatory comments in the
# source manifest, which merge through and produce a false failure.
config="${1:-Release}"
manifest=$(find "src/Bifrost.App/obj/$config" -path '*android/AndroidManifest.xml' 2>/dev/null | head -1)

if [ -z "$manifest" ]; then
  note "TC-615 skipped: no $config manifest. Run: dotnet build src/Bifrost.App -c $config"
else
  if grep -o '<uses-permission[^>]*>' "$manifest" | grep -q 'android.permission.INTERNET'; then
    bad "TC-615: INTERNET permission present in the $config manifest (NFR-306)"
    note "Debug builds add it for the debugger; Release must not ship it."
  else
    pass "TC-615: no INTERNET permission in the $config manifest"
  fi

  note "declared: $(grep -o '<uses-permission[^>]*>' "$manifest" \
        | grep -o 'android.permission.[A-Z_]*' | sort -u | tr '\n' ' ')"
fi

# ---------------------------------------------------------------- 4. TC-814 — APK size

apk=$(find "src/Bifrost.App/bin/$config" -name '*-Signed.apk' 2>/dev/null | head -1)
if [ -n "$apk" ]; then
  bytes=$(stat -c%s "$apk" 2>/dev/null || stat -f%z "$apk")
  mb=$((bytes / 1048576))
  [ "$mb" -le 30 ] \
    && pass "TC-814: APK ${mb} MB, within the 30 MB budget" \
    || bad "TC-814: APK ${mb} MB exceeds the 30 MB budget (IMP-01 §6)"
fi

echo
[ "$fail" -eq 0 ] && echo "All boundary checks passed." || echo "Boundary checks FAILED."
exit "$fail"
