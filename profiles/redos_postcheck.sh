#!/bin/bash
# Профиль пост-проверки RED OS. Выполняется на целевом сервере под root после reboot
# (или сразу после обновления, если reboot не требовался).
# Печатает машинный маркер RUNNING_KERNEL - его Update-Servers.ps1 сравнивает с EXPECTED_KERNEL.

echo "=== Пост-проверка ==="
echo "Текущее загруженное ядро:"
uname -r
echo
echo "Ядро по умолчанию:"
grubby --default-kernel
grubby --default-index
echo
echo "Установленные kernel-lt:"
rpm -q kernel-lt | sort -V
echo
echo "Установленные пакеты kernel:"
dnf list installed 2>/dev/null | grep -E '^kernel-' || true
echo
echo "Аптайм:"
uptime

echo
echo "Состояние systemd:"
system_state="$(systemctl is-system-running 2>/dev/null || true)"
echo "${system_state:-unknown}"
systemctl --failed --no-legend --no-pager 2>/dev/null || true

services_ok=1
SERVICES="${SERVICES:-}"
if [ -n "${SERVICES// /}" ]; then
  echo
  echo "Проверка критических сервисов: $SERVICES"
  set -f
  for mask in $SERVICES; do
    units="$(systemctl list-units --type=service --all --no-legend --no-pager "$mask" "${mask}.service" 2>/dev/null | awk '{print $1}' | sort -u)"
    if [ -z "$units" ]; then
      echo "Пропуск: по маске $mask сервисы на узле не найдены"
      continue
    fi
    for unit in $units; do
      if systemctl is-active --quiet "$unit"; then
        echo "OK: $unit active"
      else
        echo "ОШИБКА: $unit не active"
        services_ok=0
      fi
    done
  done
fi

# Машинный маркер для парсера
echo "RUNNING_KERNEL: $(uname -r)"
if [ "$services_ok" -ne 1 ] || [ "$system_state" = "maintenance" ] || [ "$system_state" = "stopping" ] || [ "$system_state" = "offline" ]; then
  echo "POSTCHECK_RESULT: FAILED"
elif [ "$system_state" != "running" ]; then
  echo "POSTCHECK_RESULT: WARN"
else
  echo "POSTCHECK_RESULT: OK"
fi
