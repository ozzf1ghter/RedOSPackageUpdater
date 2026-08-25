#!/bin/bash
# Проверка реально доступных исправлений безопасности RED OS через штатные
# metadata updateinfo. Узлу не нужны Trivy и отдельная база уязвимостей.

export LC_ALL=C
export COLUMNS=240

os_pretty="${RPU_OS_PRETTY_OVERRIDE:-$(. /etc/os-release 2>/dev/null; echo "${PRETTY_NAME:-unknown}")}"
os_version="${RPU_OS_VERSION_OVERRIDE:-$(. /etc/os-release 2>/dev/null; echo "${VERSION_ID:-unknown}")}"
echo "OS_INFO|$os_pretty|$(uname -r)|dnf $(dnf --version 2>/dev/null | head -1)"
echo "VULN_ENGINE|REDOS_UPDATEINFO|$os_version"

if ! command -v dnf >/dev/null 2>&1; then
  echo "PKGOP_ERR|DNF отсутствует: проверка RED OS security advisory невозможна"
  echo "PKGOP_RESULT: FAIL"
  echo "REBOOT_RECOMMENDED: no"
  exit 1
fi

work="$(mktemp -d /tmp/rpu-advisory.XXXXXX)" || exit 1
cleanup() { rm -rf "$work"; }
trap cleanup EXIT HUP INT TERM

echo "=== DNF: проверяю доступные security advisory ==="
if ! dnf -q updateinfo list --security >"$work/list" 2>"$work/error"; then
  sed 's/^/DNF_ERR|/' "$work/error"
  echo "PKGOP_ERR|DNF updateinfo завершился с ошибкой; результат недостоверен"
  echo "PKGOP_RESULT: FAIL"
  echo "REBOOT_RECOMMENDED: no"
  exit 1
fi

# Список установленных имён, от самых длинных к коротким: NEVRA нельзя делить
# по первому дефису, поскольку дефисы допустимы в имени пакета.
rpm -qa --qf '%{NAME}\n' 2>/dev/null | sort -u | awk '{print length($0) "|" $0}' |
  sort -t'|' -k1,1nr | cut -d'|' -f2- >"$work/names"

# updateinfo list: advisory, уровень/тип, NEVRA. Оставляем только строки,
# похожие на advisory, чтобы заголовки и диагностические сообщения не стали
# ложными пакетами.
awk 'NF >= 3 && $1 ~ /^[A-Za-z]+[-:][A-Za-z0-9_.:-]+$/ { print $1 "|" $2 "|" $NF }' \
  "$work/list" | sort -u >"$work/advisories"

total=0
critical=0
high=0
while IFS='|' read -r advisory raw_severity nevra; do
  [ -z "$advisory" ] && continue
  package=""
  while IFS= read -r candidate; do
    case "$nevra" in "$candidate"-*) package="$candidate"; break;; esac
  done <"$work/names"
  [ -z "$package" ] && continue

  fixed="${nevra#${package}-}"
  fixed="${fixed%.x86_64}"; fixed="${fixed%.noarch}"; fixed="${fixed%.aarch64}"
  fixed="${fixed%.i686}"; fixed="${fixed%.i586}"; fixed="${fixed%.ppc64le}"
  installed="$(rpm -q --qf '%{EVR}\n' "$package" 2>/dev/null | sort -V | tail -1)"
  [ -z "$installed" ] && continue

  severity="UNKNOWN"
  case "$raw_severity" in
    *Critical*|*Крит*) severity="CRITICAL";;
    *Important*|*Высок*) severity="HIGH";;
    *Moderate*|*Средн*) severity="MEDIUM";;
    *Low*|*Низк*) severity="LOW";;
  esac

  info_file="$work/info.$(printf '%s' "$advisory" | tr -c 'A-Za-z0-9_.-' '_')"
  if ! dnf -q updateinfo info "$advisory" >"$info_file" 2>>"$work/error"; then
    echo "DNF_WARN|Не удалось получить описание advisory $advisory"
    continue
  fi
  info_severity="$(sed -nE 's/^[[:space:]]*(Severity|Опасность)[[:space:]]*:[[:space:]]*([^[:space:]]+).*/\2/ip' "$info_file" | head -1)"
  case "$info_severity" in
    *Critical*|*Крит*) severity="CRITICAL";;
    *Important*|*Высок*) severity="HIGH";;
    *Moderate*|*Средн*) severity="MEDIUM";;
    *Low*|*Низк*) severity="LOW";;
  esac
  cves="$(grep -Eio 'CVE-[0-9]{4}-[0-9]+' "$info_file" | tr '[:lower:]' '[:upper:]' | sort -u)"
  [ -z "$cves" ] && continue
  for cve in $cves; do
    echo "VULN|$cve|$package|$installed|$fixed|$severity|RED OS security advisory $advisory"
    echo "VULN_ALIAS|$cve|$package|$advisory"
    total=$((total+1))
    [ "$severity" = "CRITICAL" ] && critical=$((critical+1))
    [ "$severity" = "HIGH" ] && high=$((high+1))
  done
done <"$work/advisories"

echo "VULN_SUMMARY|$total|0|$critical|$high"
echo "PKGOP_RESULT: OK"
echo "REBOOT_RECOMMENDED: no"
