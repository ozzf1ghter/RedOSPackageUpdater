#!/bin/bash
# Профиль: RED OS - обновление kernel-lt + security-обновления (postgresql исключён намеренно).
# Выполняется на целевом сервере (RED OS) под root, скармливается через: plink ... "bash -s" < этот_файл
#
# В конце печатает машинные маркеры для контроля со стороны Update-Servers.ps1:
#   RESULT: READY_FOR_REBOOT | DO_NOT_REBOOT   - можно ли вообще перезагружать
#   REBOOT_REQUIRED: yes | no                  - нужен ли reboot по факту обновлений
#   EXPECTED_KERNEL: <версия ядра, которая должна загрузиться после reboot>
# Русский текст - для человека в логе, парсер ориентируется только на ASCII-маркеры.
# Софтина перезагружает хост только если RESULT=READY_FOR_REBOOT И REBOOT_REQUIRED=yes.

echo "=== Исходное состояние ==="
# ОС узла - машиночитаемый маркер для GUI (парк RED OS может быть смешанным: 7.3 и 8 одновременно).
osname="$( (. /etc/os-release 2>/dev/null; echo "$PRETTY_NAME") 2>/dev/null)"
[ -z "$osname" ] && osname="неизвестно"
echo "OS_INFO|$osname|$(uname -r)|$(dnf --version 2>/dev/null | head -1 | tr -d '\n')"
uname -r
grubby --default-kernel
grubby --default-index
rpm -q kernel-lt kernel-lt-tools kernel-lt-tools-libs 2>/dev/null | sort -V
echo

echo "=== Резервная копия перед обновлением ==="
# Ротация: оставляем последние BACKUP_KEEP бэкапов, старые удаляем
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
command -v grubby >/dev/null 2>&1 && grubby --info=ALL > "$bkp/grubby-info.before.txt" 2>/dev/null
cp -a /etc/dnf/dnf.conf "$bkp/" 2>/dev/null
[ -f /etc/sysconfig/kernel ] && cp -a /etc/sysconfig/kernel "$bkp/" 2>/dev/null
yum history list 2>/dev/null | head -8 > "$bkp/yum-history.before.txt"
echo "Бэкап: $bkp (откат security: yum history undo <id>)"
echo

echo "=== Настраиваем kernel-lt и лимит ядер ==="
cp -a /etc/sysconfig/kernel /etc/sysconfig/kernel.bak.$(date +%F_%H%M%S)
cp -a /etc/dnf/dnf.conf /etc/dnf/dnf.conf.bak.$(date +%F_%H%M%S)
grep -q '^UPDATEDEFAULT=' /etc/sysconfig/kernel \
  && sed -i 's/^UPDATEDEFAULT=.*/UPDATEDEFAULT=yes/' /etc/sysconfig/kernel \
  || sed -i '1iUPDATEDEFAULT=yes' /etc/sysconfig/kernel
grep -q '^DEFAULTKERNEL=' /etc/sysconfig/kernel \
  && sed -i 's/^DEFAULTKERNEL=.*/DEFAULTKERNEL=kernel-lt/' /etc/sysconfig/kernel \
  || echo 'DEFAULTKERNEL=kernel-lt' >> /etc/sysconfig/kernel
grep -q '^installonly_limit=' /etc/dnf/dnf.conf \
  && sed -i 's/^installonly_limit=.*/installonly_limit=3/' /etc/dnf/dnf.conf \
  || echo 'installonly_limit=3' >> /etc/dnf/dnf.conf

echo "=== Обновляем метаданные репозиториев (чтобы видеть свежее зеркало, как в предпроверке) ==="
yum clean expire-cache >/dev/null 2>&1 || true
yum -q makecache >/dev/null 2>&1 || true

echo "=== Обновляем ядро ==="
yum check-update kernel-lt kernel-lt-tools kernel-lt-tools-libs || true
if ! yum -y update kernel-lt kernel-lt-tools kernel-lt-tools-libs; then
  echo "ОШИБКА: обновление kernel-lt завершилось с ошибкой"
  echo "RESULT: DO_NOT_REBOOT"
  echo "REBOOT_REQUIRED: no"
  echo "EXPECTED_KERNEL: unknown"
  exit 1
fi

echo "=== Устанавливаем security-обновления ==="
EXCLUDE="${EXCLUDE-postgresql*}"   # без ":" - явно пустой список из GUI уважаем
exargs=""
set -f                             # маски не разворачиваем в имена файлов
for m in $EXCLUDE; do exargs="$exargs --exclude=$m"; done
set +f
echo "ИСКЛЮЧЕНЫ из обновления (маски): ${EXCLUDE:-(нет)}"
# Провал security-транзакции НЕ глушим: иначе узел рапортует OK, а патчи не легли (уязвим).
if ! yum -y update --security $exargs; then
  echo "ОШИБКА: security-обновление завершилось с ошибкой (патчи не применены)"
  echo "RESULT: DO_NOT_REBOOT"
  echo "REBOOT_REQUIRED: no"
  echo "EXPECTED_KERNEL: $(uname -r)"
  exit 1
fi

# Проверяем, что kernel-lt реально установлен, прежде чем строить путь
if ! rpm -q kernel-lt >/dev/null 2>&1; then
  echo "ОШИБКА: пакет kernel-lt не установлен"
  echo "RESULT: DO_NOT_REBOOT"
  echo "REBOOT_REQUIRED: no"
  echo "EXPECTED_KERNEL: unknown"
  exit 1
fi

latest_ver="$(rpm -q --qf '%{VERSION}-%{RELEASE}.%{ARCH}\n' kernel-lt | sort -V | tail -1)"
latest_kernel="/boot/vmlinuz-${latest_ver}"

echo "=== Проверяем default kernel ==="
echo "Последнее kernel-lt: $latest_kernel"
echo "Текущий default: $(grubby --default-kernel)"
if [ ! -e "$latest_kernel" ]; then
  echo "ОШИБКА: файл $latest_kernel не найден, default не меняю"
  echo "RESULT: DO_NOT_REBOOT"
  echo "REBOOT_REQUIRED: no"
  echo "EXPECTED_KERNEL: unknown"
  exit 1
fi
[ "$(grubby --default-kernel)" != "$latest_kernel" ] && grubby --set-default "$latest_kernel"

# Извлечение путей ядер из grubby, устойчивое к кавычкам (на BLS grubby выводит kernel="/boot/...")
extract_kernels() {
  grubby --info=ALL | awk -F= '/^kernel=/{gsub(/"/,"",$2); print $2}'
}

echo "=== Проверяем GRUB на отсутствующие ядра ==="
missing=0
while read -r k; do
  [ -n "$k" ] || continue
  [ -e "$k" ] || { echo "Отсутствует: $k"; missing=1; }
done < <(extract_kernels)

if [ "$missing" -ne 0 ]; then
  echo "=== Чистим устаревшие BLS/GRUB записи ==="
  [ -f /boot/grub2/grub.cfg ] && cp -a /boot/grub2/grub.cfg /boot/grub2/grub.cfg.bak.$(date +%F_%H%M%S)
  [ -f /boot/efi/EFI/redos/grub.cfg ] && cp -a /boot/efi/EFI/redos/grub.cfg /boot/efi/EFI/redos/grub.cfg.bak.$(date +%F_%H%M%S)
  backup_dir="/root/old-bls-entries.$(date +%F_%H%M%S)"
  mkdir -p "$backup_dir"
  for f in /boot/loader/entries/*.conf; do
    [ -f "$f" ] || continue
    kernel_path="$(awk '/^linux /{print $2}' "$f" | head -1)"
    [ -z "$kernel_path" ] && continue
    case "$kernel_path" in
      /vmlinuz-*) real_path="/boot$kernel_path" ;;
      /boot/vmlinuz-*) real_path="$kernel_path" ;;
      *) continue ;;
    esac
    [ ! -e "$real_path" ] && mv "$f" "$backup_dir"/
  done
  [ -f /boot/grub2/grub.cfg ] && grub2-mkconfig -o /boot/grub2/grub.cfg
  [ -f /boot/efi/EFI/redos/grub.cfg ] && grub2-mkconfig -o /boot/efi/EFI/redos/grub.cfg
  grubby --set-default "$latest_kernel"
fi

echo "=== Финальная проверка ==="
uname -r
grubby --default-kernel
grubby --default-index
rpm -q kernel-lt | sort -V
grubby --info=ALL | egrep '^(index|kernel|title)='

echo "=== Проверяем отсутствующие ядра после чистки ==="
missing=0
while read -r k; do
  [ -n "$k" ] || continue
  [ -e "$k" ] || { echo "Отсутствует: $k"; missing=1; }
done < <(extract_kernels)

# Ожидаемое ядро - то, что ДОЛЖНО загрузиться (последнее kernel-lt), а не то, что сейчас в default.
# Иначе, если grubby --set-default молча не сработал, ожидаемое = старое ядро и postcheck ложно "проходит".
# При таком раскладе postcheck увидит RUNNING != latest_ver -> MISMATCH/Warn, а не мнимый OK.
expected="$latest_ver"
[ -z "$expected" ] && expected="$(grubby --default-kernel 2>/dev/null | sed 's#^/boot/vmlinuz-##')"
[ -z "$expected" ] && expected="$(uname -r)"
def_now="$(grubby --default-kernel 2>/dev/null | sed 's#^/boot/vmlinuz-##')"
[ -n "$def_now" ] && [ "$def_now" != "$expected" ] && echo "ВНИМАНИЕ: default-ядро ($def_now) != ожидаемого ($expected) - grubby мог не переключить default"

# Нужен ли reboot по факту. Основной сигнал - needs-restarting -r (yum-utils/dnf-utils).
# Если утилиты нет - пробуем доставить (пакет dnf-utils, стандартный, не зависит от версии ОС),
# чтобы не сваливаться на более грубый fallback без явной необходимости.
if ! command -v needs-restarting >/dev/null 2>&1; then
  echo "needs-restarting не найден - пробую поставить dnf-utils"
  yum -y install dnf-utils >/dev/null 2>&1 || true
fi
reboot_required=no
if command -v needs-restarting >/dev/null 2>&1; then
  if ! needs-restarting -r >/dev/null 2>&1; then
    reboot_required=yes
  fi
else
  echo "ВНИМАНИЕ: needs-restarting недоступен (dnf-utils не встал) - решаю по ядру, менее точно"
  [ "$expected" != "$(uname -r)" ] && reboot_required=yes
fi

if [ "$missing" -eq 0 ]; then
  echo "Записей GRUB на отсутствующие ядра не найдено. Можно выполнять reboot."
  echo "RESULT: READY_FOR_REBOOT"
else
  echo "Есть отсутствующие ядра. Reboot пока не делать."
  echo "RESULT: DO_NOT_REBOOT"
fi
echo "REBOOT_REQUIRED: ${reboot_required}"
echo "EXPECTED_KERNEL: ${expected}"
