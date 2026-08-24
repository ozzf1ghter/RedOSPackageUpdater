#!/bin/bash
# Профиль: корректная остановка критичных сервисов ПЕРЕД reboot (RED OS, root).
# Список масок сервисов подставляет софтина в переменную SERVICES (через пробел),
# например: SERVICES="postgresql* patroni pgbouncer".
# Имя PostgreSQL не хардкодится - маски разворачиваются в реальные unit'ы через systemd.
# Маркер: PRESTOP_RESULT: OK | FAILED. При FAILED софтина НЕ должна выполнять reboot.

SERVICES="${SERVICES:-}"
STOP_TIMEOUT="${STOP_TIMEOUT:-120}"   # секунд на остановку одного сервиса

echo "=== Анализ запущенных тяжёлых сервисов (для сведения) ==="
running="$(systemctl list-units --type=service --state=running --no-legend --no-pager 2>/dev/null | awk '{print $1}')"
echo "$running" | grep -Ei 'postgres|patroni|pgbouncer|mysql|mariadb|mongo|redis|rabbitmq|docker|clickhouse|oracle|1c|zabbix' \
  && echo "(^ найдены сервисы БД/приложений, убедитесь что нужные попали в список остановки)" \
  || echo "(типовых БД/тяжёлых сервисов не обнаружено)"
echo

overall_ok=1
stopped=0

if [ -z "${SERVICES// /}" ]; then
  echo "Список сервисов для остановки пуст - ничего не останавливаю"
  echo "PRESTOP_RESULT: OK"
  exit 0
fi

echo "=== Останавливаем сервисы по маскам: $SERVICES ==="
set -f   # маски (postgresql*) не разворачиваем по файлам - только systemd их трактует
for mask in $SERVICES; do
  # Разворачиваем маску в реальные unit'ы (учитываем и .service)
  units="$(systemctl list-units --type=service --all --no-legend --no-pager "$mask" 2>/dev/null | awk '{print $1}')"
  if [ -z "$units" ]; then
    units="$(systemctl list-units --type=service --all --no-legend --no-pager "${mask}.service" 2>/dev/null | awk '{print $1}')"
  fi
  # Если systemd ничего не нашёл по маске - пробуем как есть (точное имя)
  [ -z "$units" ] && units="$mask"

  for u in $units; do
    if systemctl is-active --quiet "$u"; then
      echo "Останавливаю $u (корректно, через systemd)..."
      systemctl stop "$u"
      t=0
      while systemctl is-active --quiet "$u"; do
        sleep 2; t=$((t+2))
        [ "$t" -ge "$STOP_TIMEOUT" ] && break
      done
      if systemctl is-active --quiet "$u"; then
        echo "ОШИБКА: $u не остановился за ${STOP_TIMEOUT}s"
        overall_ok=0
      else
        echo "OK: остановлен $u за ${t}s"
        stopped=$((stopped+1))
      fi
    else
      echo "Пропуск: $u не запущен"
    fi
  done
done

if [ "$stopped" -eq 0 ]; then
  echo "Из списка масок активных сервисов на хосте не было - останавливать нечего"
else
  echo "Остановлено сервисов: $stopped"
fi
if [ "$overall_ok" -eq 1 ]; then
  echo "PRESTOP_RESULT: OK"
else
  echo "PRESTOP_RESULT: FAILED"
fi
