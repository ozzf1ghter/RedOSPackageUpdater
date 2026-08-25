#!/bin/bash
# Профиль: RED OS - только security-обновления, БЕЗ трогания ядра и GRUB.
# postgresql исключён НАМЕРЕННО. Выполняется на сервере под root.
# Маркеры для контроля: RESULT / REBOOT_REQUIRED / EXPECTED_KERNEL.

echo "=== Исходное состояние ==="
osname="$( (. /etc/os-release 2>/dev/null; echo "$PRETTY_NAME") 2>/dev/null)"
[ -z "$osname" ] && osname="неизвестно"
osver="$( (. /etc/os-release 2>/dev/null; echo "$VERSION_ID") 2>/dev/null)"
case "$osver" in 7.3|8.0) ;; *) echo "ОШИБКА: RED OS ${osver:-unknown} пока не поддерживается этим профилем"; exit 1;; esac
command -v dnf >/dev/null 2>&1 || { echo "ОШИБКА: DNF не найден"; exit 1; }
echo "OS_INFO|$osname|$(uname -r)|$(dnf --version 2>/dev/null | head -1 | tr -d '\n')"
uname -r
echo

echo "=== Резервная копия перед обновлением ==="
KEEP="${BACKUP_KEEP:-5}"
old=$(ls -1dt /root/rpu-backup-* 2>/dev/null | tail -n +$((KEEP+1)))
if [ -n "$old" ]; then
  echo "Чищу старые бэкапы (оставляю последние $KEEP):"
  echo "$old" | while read -r d; do echo "  удаляю $d"; rm -rf "$d"; done
fi
bkp="/root/rpu-backup-$(date +%F_%H%M%S)"
mkdir -p "$bkp"
rpm -qa | sort > "$bkp/rpm-qa.before.txt" 2>/dev/null
uname -a > "$bkp/uname.txt" 2>/dev/null
cp -a /etc/dnf/dnf.conf "$bkp/" 2>/dev/null
dnf history list 2>/dev/null | head -8 > "$bkp/dnf-history.before.txt"
echo "Бэкап: $bkp (откат security: dnf history undo <id>)"
echo

echo "=== Обновляем метаданные репозиториев (чтобы видеть свежее зеркало, как в предпроверке) ==="
dnf clean expire-cache >/dev/null 2>&1 || true
dnf -q makecache >/dev/null 2>&1 || true

echo "=== Устанавливаем security-обновления ==="
EXCLUDE="${EXCLUDE-postgresql*}"   # без ":" - явно пустой список из GUI уважаем
exargs=""
set -f                             # маски не разворачиваем в имена файлов
for m in $EXCLUDE; do exargs="$exargs --exclude=$m"; done
set +f
echo "ИСКЛЮЧЕНЫ из обновления (маски): ${EXCLUDE:-(нет)}"
cp -a /etc/dnf/dnf.conf /etc/dnf/dnf.conf.bak.$(date +%F_%H%M%S) 2>/dev/null || true
dnf check-update --security $exargs || true
if ! dnf -y update --security $exargs; then
  echo "ОШИБКА: security-обновление завершилось с ошибкой"
  echo "RESULT: DO_NOT_REBOOT"
  echo "REBOOT_REQUIRED: no"
  echo "EXPECTED_KERNEL: $(uname -r)"
  exit 1
fi

echo "=== Финальная проверка ==="
uname -r
dnf list installed 2>/dev/null | grep -E '^kernel-' || true

# Ожидаемое ядро (ядро не трогали - остаётся текущее загруженное/или default)
expected="$(uname -r)"
if command -v grubby >/dev/null 2>&1; then
  d="$(grubby --default-kernel 2>/dev/null | sed 's#^/boot/vmlinuz-##')"
  [ -n "$d" ] && expected="$d"
fi

# Нужен ли reboot по факту security-обновлений (glibc/systemd/openssl и т.п.)
reboot_required=no
if command -v needs-restarting >/dev/null 2>&1; then
  if ! needs-restarting -r >/dev/null 2>&1; then
    reboot_required=yes
  fi
elif dnf needs-restarting --help >/dev/null 2>&1; then
  if ! dnf needs-restarting -r >/dev/null 2>&1; then reboot_required=yes; fi
else
  echo "ВНИМАНИЕ: needs-restarting недоступен. Reboot-статус определить точно нельзя."
  # Консервативно: если default-ядро отличается от загруженного - нужен reboot
  [ "$expected" != "$(uname -r)" ] && reboot_required=yes
fi

echo "RESULT: READY_FOR_REBOOT"
echo "REBOOT_REQUIRED: ${reboot_required}"
echo "EXPECTED_KERNEL: ${expected}"
