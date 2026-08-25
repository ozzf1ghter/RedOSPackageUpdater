#!/bin/bash
set -eu
PATH="/usr/bin:/bin:$PATH"

root="$(mktemp -d)"
trap 'rm -rf "$root"' EXIT
mkdir -p "$root/bin"

cat >"$root/bin/dnf" <<'EOF'
#!/bin/bash
if [ "$1" = "--version" ]; then echo 4.17.0; exit 0; fi
if [ "$1" = "-q" ] && [ "$2" = "updateinfo" ] && [ "$3" = "list" ]; then
  echo 'ROS-20260825-01 Critical/Sec. cups-1:2.4.7-6.red80.x86_64'
  exit 0
fi
if [ "$1" = "-q" ] && [ "$2" = "updateinfo" ] && [ "$3" = "info" ]; then
  echo 'Update ID: ROS-20260825-01'
  echo 'Severity: Critical'
  echo 'CVEs: CVE-2026-39316 CVE-2026-39317'
  exit 0
fi
exit 2
EOF

cat >"$root/bin/rpm" <<'EOF'
#!/bin/bash
if [ "$1" = "-qa" ]; then echo cups; exit 0; fi
if [ "$1" = "-q" ]; then echo '1:2.4.7-3.red80'; exit 0; fi
exit 2
EOF
chmod +x "$root/bin/dnf" "$root/bin/rpm"

out="$(PATH="$root/bin:$PATH" RPU_OS_PRETTY_OVERRIDE='RED OS 8.0' RPU_OS_VERSION_OVERRIDE='8.0' bash profiles/redos_advisory_scan.sh)"
grep -q '^VULN|CVE-2026-39316|cups|1:2.4.7-3.red80|1:2.4.7-6.red80|CRITICAL|RED OS security advisory ROS-20260825-01$' <<<"$out"
grep -q '^VULN_ALIAS|CVE-2026-39316|cups|ROS-20260825-01$' <<<"$out"
grep -q '^VULN_SUMMARY|2|0|2|0$' <<<"$out"
grep -q '^PKGOP_RESULT: OK$' <<<"$out"
echo 'OK   DNF advisory parser RED OS 8 fixture'
