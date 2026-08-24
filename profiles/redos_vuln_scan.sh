#!/bin/bash
# Проверка установленной RED OS по базе БДУ ФСТЭК через штатный Trivy RED OS.
# При отсутствии Trivy устанавливает его из настроенного репозитория узла.

export LC_ALL=C

# Блокируем весь цикл (обновление локальной базы, установка Trivy и сканирование), чтобы два
# экземпляра программы не меняли кеш и не нагружали диск одного узла одновременно.
if command -v flock >/dev/null 2>&1; then
  exec 9>/run/rpu-trivy-scan.lock
  if ! flock -n 9; then
    echo "PKGOP_ERR|На узле уже выполняется другая проверка Trivy"
    echo "PKGOP_RESULT: FAIL"
    echo "REBOOT_RECOMMENDED: no"
    exit 1
  fi
fi

# База доставляется Windows-программой по SFTP. Серверу интернет не требуется.
cache_root="/root/.cache/trivy"
cache_dir="$cache_root/db"
if [ -n "${VULN_DB_ARCHIVE:-}" ]; then
  mkdir -p "$cache_root" || exit 1
  if [ -n "${VULN_DB_DIGEST:-}" ] && command -v sha256sum >/dev/null 2>&1; then
    archive_digest="$(sha256sum "$VULN_DB_ARCHIVE" 2>/dev/null | awk '{print $1}')"
    if [ "$archive_digest" != "$VULN_DB_DIGEST" ]; then
      rm -f "$VULN_DB_ARCHIVE"
      echo "PKGOP_ERR|Контрольная сумма переданной базы ФСТЭК не совпадает"
      echo "PKGOP_RESULT: FAIL"
      echo "REBOOT_RECOMMENDED: no"
      exit 1
    fi
  fi
  stage="$(mktemp -d "$cache_root/db-stage.XXXXXX")" || exit 1
  # Не распаковываем архив с абсолютными путями, '..' или ссылками: импортируемый файл может
  # поступить через съёмный носитель, а скрипт выполняется под root.
  if tar -tzf "$VULN_DB_ARCHIVE" | grep -Eq '(^/|(^|/)\.\.(/|$))' ||
     tar -tvzf "$VULN_DB_ARCHIVE" | awk '$1 ~ /^[lh]/ { bad=1 } END { exit bad ? 0 : 1 }'; then
    rm -rf "$stage"; rm -f "$VULN_DB_ARCHIVE"
    echo "PKGOP_ERR|Архив базы содержит небезопасные пути или ссылки"
    echo "PKGOP_RESULT: FAIL"
    echo "REBOOT_RECOMMENDED: no"
    exit 1
  fi
  if ! tar -xzf "$VULN_DB_ARCHIVE" -C "$stage"; then
    rm -rf "$stage" "$VULN_DB_ARCHIVE"
    echo "PKGOP_ERR|Не удалось распаковать переданную базу ФСТЭК"
    echo "PKGOP_RESULT: FAIL"
    echo "REBOOT_RECOMMENDED: no"
    exit 1
  fi
  # OCI-слой обычно содержит trivy.db и metadata.json в корне.
  dbfile="$(find "$stage" -type f -name trivy.db -print -quit)"
  metafile="$(find "$stage" -type f -name metadata.json -print -quit)"
  if [ -z "$dbfile" ]; then
    rm -rf "$stage" "$VULN_DB_ARCHIVE"
    echo "PKGOP_ERR|В архиве не найден trivy.db"
    echo "PKGOP_RESULT: FAIL"
    echo "REBOOT_RECOMMENDED: no"
    exit 1
  fi
  newdir="$cache_root/db-new.$$"
  rm -rf "$newdir"; mkdir -m 0700 "$newdir" || exit 1
  install -m 0600 "$dbfile" "$newdir/trivy.db"
  [ -n "$metafile" ] && install -m 0600 "$metafile" "$newdir/metadata.json"
  rm -rf "$cache_root/db-old"
  [ -d "$cache_dir" ] && mv "$cache_dir" "$cache_root/db-old"
  if ! mv "$newdir" "$cache_dir"; then
    [ -d "$cache_root/db-old" ] && mv "$cache_root/db-old" "$cache_dir"
    rm -rf "$stage" "$newdir"; rm -f "$VULN_DB_ARCHIVE"
    echo "PKGOP_ERR|Не удалось атомарно заменить базу Trivy"
    echo "PKGOP_RESULT: FAIL"
    echo "REBOOT_RECOMMENDED: no"
    exit 1
  fi
  rm -rf "$cache_root/db-old" "$stage"; rm -f "$VULN_DB_ARCHIVE"
  printf '%s' "${VULN_DB_DIGEST:-unknown}" > "$cache_root/rpu-db.digest.tmp"
  mv -f "$cache_root/rpu-db.digest.tmp" "$cache_root/rpu-db.digest"
fi

if ! command -v trivy >/dev/null 2>&1; then
  echo "=== Trivy не установлен, устанавливаем из репозитория узла ==="
  installer=""
  command -v dnf >/dev/null 2>&1 && installer="dnf"
  [ -z "$installer" ] && command -v yum >/dev/null 2>&1 && installer="yum"
  if [ -z "$installer" ] || ! "$installer" -y install trivy; then
    echo "PKGOP_ERR|Trivy отсутствует и не установлен: проверьте наличие пакета trivy во внутреннем репозитории"
    echo "PKGOP_RESULT: FAIL"
    echo "REBOOT_RECOMMENDED: no"
    exit 1
  fi
  hash -r
  if ! command -v trivy >/dev/null 2>&1; then
    echo "PKGOP_ERR|Менеджер пакетов завершился без ошибки, но команда trivy не появилась"
    echo "PKGOP_RESULT: FAIL"
    echo "REBOOT_RECOMMENDED: no"
    exit 1
  fi
  echo "TRIVY_INSTALLED: yes"
else
  echo "TRIVY_INSTALLED: no"
fi

echo "OS_INFO|$(. /etc/os-release 2>/dev/null; echo "${PRETTY_NAME:-unknown}")|$(uname -r)|trivy $(trivy --version 2>/dev/null | head -1)"

tmpl_base="$(mktemp /tmp/rpu-trivy-template.XXXXXX)" || exit 1
tmpl="${tmpl_base}.tpl"
if ! mv "$tmpl_base" "$tmpl"; then
  rm -f "$tmpl_base"
  echo "PKGOP_ERR|Не удалось подготовить шаблон отчёта Trivy"
  echo "PKGOP_RESULT: FAIL"
  echo "REBOOT_RECOMMENDED: no"
  exit 1
fi
cat > "$tmpl" <<'EOF'
{{ range . }}{{ range .Vulnerabilities }}{{ $v := . }}VULN|{{ .VulnerabilityID }}|{{ .PkgName }}|{{ .InstalledVersion }}|{{ .FixedVersion }}|{{ .Severity }}|{{ .Title }}
VULN_URL|{{ .VulnerabilityID }}|{{ .PkgName }}|{{ .PrimaryURL }}
VULN_DATE|{{ .VulnerabilityID }}|{{ .PkgName }}|{{ .PublishedDate }}|{{ .LastModifiedDate }}
{{ range .VendorIDs }}VULN_ALIAS|{{ $v.VulnerabilityID }}|{{ $v.PkgName }}|{{ . }}
{{ end }}{{ range .Vulnerability.References }}VULN_REF|{{ $v.VulnerabilityID }}|{{ $v.PkgName }}|{{ . }}
{{ end }}{{ end }}{{ end }}
EOF

# В сборке Trivy от RED SOFT ключ --using-bdu добавляет базу БДУ ФСТЭК.
# При первом запуске базы загружаются автоматически; далее используется локальный кеш ~/.cache/trivy.
out="$(mktemp /tmp/rpu-trivy-output.XXXXXX)" || {
  rm -f "$tmpl"
  echo "PKGOP_ERR|Не удалось создать временный файл результата Trivy"
  echo "PKGOP_RESULT: FAIL"; echo "REBOOT_RECOMMENDED: no"; exit 1
}
err="$(mktemp /tmp/rpu-trivy-error.XXXXXX)" || {
  rm -f "$tmpl" "$out"
  echo "PKGOP_ERR|Не удалось создать временный файл журнала Trivy"
  echo "PKGOP_RESULT: FAIL"; echo "REBOOT_RECOMMENDED: no"; exit 1
}
scan_root="$(mktemp -d /tmp/rpu-trivy-root.XXXXXX)" || {
  rm -f "$tmpl" "$out" "$err"
  echo "PKGOP_ERR|Не удалось создать минимальный снимок ОС для Trivy"
  echo "PKGOP_RESULT: FAIL"; echo "REBOOT_RECOMMENDED: no"; exit 1
}
trivy_pid=""
scan_id="${RPU_SCAN_ID:-$$}"
case "$scan_id" in *[!a-zA-Z0-9_-]*) scan_id="$$";; esac
pid_file="/run/rpu-trivy-${scan_id}.pid"
cleanup_scan() {
  if [ -n "$trivy_pid" ] && kill -0 "$trivy_pid" 2>/dev/null; then
    kill -TERM "$trivy_pid" 2>/dev/null || true
    sleep 1
    kill -KILL "$trivy_pid" 2>/dev/null || true
    wait "$trivy_pid" 2>/dev/null || true
  fi
  rm -f "$pid_file" "$tmpl" "$out" "$err"
  rm -rf "$scan_root"
}
abort_scan() { exit 130; }
trap cleanup_scan EXIT
trap abort_scan HUP INT TERM

# Создаём минимальный rootfs: только идентификаторы ОС и RPM DB. В отличие от сканирования '/'
# это принципиально не зависит от расположения пользовательских БД, бэкапов и сетевых ресурсов.
mkdir -p "$scan_root/etc" "$scan_root/usr/lib" "$scan_root/var/lib" || {
  echo "PKGOP_ERR|Не удалось подготовить структуру минимального снимка ОС"
  echo "PKGOP_RESULT: FAIL"; echo "REBOOT_RECOMMENDED: no"; exit 1
}
for os_file in /etc/os-release /etc/redhat-release /etc/system-release; do
  [ -f "$os_file" ] && cp -L "$os_file" "$scan_root/etc/$(basename "$os_file")"
done
if [ -f /usr/lib/os-release ]; then
  mkdir -p "$scan_root/usr/lib"
  cp -L /usr/lib/os-release "$scan_root/usr/lib/os-release"
fi

rpm_db="$(rpm --eval '%{_dbpath}' 2>/dev/null)"
case "$rpm_db" in
  /var/lib/rpm) rpm_dest="$scan_root/var/lib/rpm" ;;
  /usr/lib/sysimage/rpm) rpm_dest="$scan_root/usr/lib/sysimage/rpm" ;;
  /*) rpm_dest="$scan_root/var/lib/rpm" ;;
  *) rpm_db=""; rpm_dest="" ;;
esac
if [ -z "$rpm_db" ] || [ ! -d "$rpm_db" ]; then
  echo "PKGOP_ERR|Не найдена база установленных RPM-пакетов"
  echo "PKGOP_RESULT: FAIL"; echo "REBOOT_RECOMMENDED: no"; exit 1
fi
mkdir -p "$rpm_dest"
if ! cp -aL "$rpm_db/." "$rpm_dest/"; then
  echo "PKGOP_ERR|Не удалось скопировать RPM-базу в минимальный снимок"
  echo "PKGOP_RESULT: FAIL"; echo "REBOOT_RECOMMENDED: no"; exit 1
fi
snapshot_kb="$(du -sk "$scan_root" 2>/dev/null | awk '{print $1}')"
echo "=== Trivy: минимальный снимок ОС готов (${snapshot_kb:-0} КБ), пользовательские каталоги не сканируются ==="
echo "=== Trivy: начата проверка установленных RPM-пакетов ==="
trivy --using-bdu --timeout 20m rootfs --skip-db-update --scanners vuln --pkg-types os \
  --format template --template "@$tmpl" "$scan_root" >"$out" 2>"$err" &
trivy_pid=$!
printf '%s\n' "$trivy_pid" > "$pid_file"
elapsed=0
last_activity=""
idle_checks=0
err_line=1
while kill -0 "$trivy_pid" 2>/dev/null; do
  sleep 15
  err_count="$(wc -l < "$err" 2>/dev/null || echo 0)"
  if [ "${err_count:-0}" -ge "$err_line" ]; then
    tail -n "+$err_line" "$err" 2>/dev/null | sed -e 's/[[:space:]]err\.stacktrace=.*$//' -e 's/^/TRIVY_LOG|/'
    err_line=$((err_count+1))
  fi
  if kill -0 "$trivy_pid" 2>/dev/null; then
    elapsed=$((elapsed+15))
    proc_info="$(ps -p "$trivy_pid" -o stat=,%cpu=,%mem= 2>/dev/null | xargs)"
    case "${proc_info%% *}" in
      R*) state_text="выполняется на CPU" ;;
      D*) state_text="ожидает диск/ввод-вывод" ;;
      S*) state_text="ожидает данные" ;;
      T*) state_text="приостановлен" ;;
      Z*) state_text="завершён, ожидает обработки" ;;
      *)  state_text="состояние неизвестно" ;;
    esac
    cpu_ticks="$(awk '{print $14+$15}' "/proc/$trivy_pid/stat" 2>/dev/null)"
    io_bytes="$(awk '/^(read_bytes|write_bytes):/{n+=$2} END{print n+0}' "/proc/$trivy_pid/io" 2>/dev/null)"
    activity="${cpu_ticks:-0}:${io_bytes:-0}"
    if [ "$activity" = "$last_activity" ]; then
      idle_checks=$((idle_checks+1))
    else
      idle_checks=0
      last_activity="$activity"
    fi
    if [ "$idle_checks" -ge 8 ]; then
      echo "=== Trivy: нет активности CPU/диска $((idle_checks*15)) с, возможно ожидает ресурс или завис; ${state_text}; [STAT CPU% MEM%]: ${proc_info:-нет данных} ==="
    else
      echo "=== Trivy работает: ${elapsed} с; ${state_text}; [STAT CPU% MEM%]: ${proc_info:-нет данных} ==="
    fi
  fi
done
wait "$trivy_pid"
rc=$?
trivy_pid=""
# Вывод, появившийся после последнего 15-секундного опроса.
err_count="$(wc -l < "$err" 2>/dev/null || echo 0)"
if [ "${err_count:-0}" -ge "$err_line" ]; then
  tail -n "+$err_line" "$err" 2>/dev/null | sed -e 's/[[:space:]]err\.stacktrace=.*$//' -e 's/^/TRIVY_LOG|/'
fi
if [ $rc -ne 0 ]; then
  echo "TRIVY_ERR|Trivy завершился с кодом $rc; подробности выше в журнале"
  echo "PKGOP_RESULT: FAIL"
  echo "REBOOT_RECOMMENDED: no"
  exit $rc
fi

cat "$out"
total=$(grep -c '^VULN|' "$out" 2>/dev/null || true)
bdu=$(awk -F'|' '$1=="VULN" && $2 ~ /^BDU:/{n++} END{print n+0}' "$out")
critical=$(awk -F'|' '$2 ~ /^BDU:/ && $6=="CRITICAL"{n++} END{print n+0}' "$out")
high=$(awk -F'|' '$2 ~ /^BDU:/ && $6=="HIGH"{n++} END{print n+0}' "$out")
echo "VULN_SUMMARY|$total|$bdu|$critical|$high"
echo "PKGOP_RESULT: OK"
echo "REBOOT_RECOMMENDED: no"
