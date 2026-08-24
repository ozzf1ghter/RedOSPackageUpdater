#!/bin/bash
# Управление ПРОИЗВОЛЬНЫМИ пакетами на узле (RED OS, root).
# Вход (env):
#   ACTION=install|update|remove|lock|unlock|locklist
#   PKGS="p1 p2 ..."   (имена пакетов через пробел; для locklist необязательно - пусто = все)
#   DRYRUN=1           (предпроверка, ничего не меняем)
# Маркеры для парсера:
#   CHANGED|name|old|new|action        (action: install|upgrade|downgrade|erase|блокировка|разблокировка|...)
#   PKGOP_RESULT: OK | NOTHING | FAIL
#   PKGOP_ERR|текст                    (ошибка ввода: пакет не найден и т.п.)
#   REBOOT_RECOMMENDED: yes | no
# lock/unlock - dnf versionlock (закрепить/снять версию пакета, чтобы update его не трогал).
# Ничего не перезагружает - только сообщает, нужен ли reboot по факту.

export LC_ALL=C
export COLUMNS=200   # не даём dnf переносить длинные NEVRA - иначе парсер теряет строки
set -f   # имена пакетов не разворачиваем по файлам

ACTION="${ACTION:-update}"
PKGS="${PKGS:-}"

case "$ACTION" in
  install|update|remove|lock|unlock|locklist) ;;
  *) echo "Неизвестное действие: $ACTION"; echo "PKGOP_RESULT: FAIL"; exit 1;;
esac
# для всех кроме locklist список пакетов обязателен
if [ "$ACTION" != "locklist" ] && [ -z "${PKGS// /}" ]; then
  echo "Список пакетов пуст"
  echo "PKGOP_RESULT: FAIL"
  exit 1
fi

osname="$( (. /etc/os-release 2>/dev/null; echo "$PRETTY_NAME") 2>/dev/null)"
[ -z "$osname" ] && osname="неизвестно"
echo "OS_INFO|$osname|$(uname -r)|$(dnf --version 2>/dev/null | head -1 | tr -d '\n')"
echo "=== Действие: $ACTION ; пакеты: ${PKGS:-(все)} ==="

# =====================================================================
# versionlock: закрепление/снятие/просмотр версий (dnf versionlock)
# =====================================================================
if [ "$ACTION" = "lock" ] || [ "$ACTION" = "unlock" ] || [ "$ACTION" = "locklist" ]; then
  # путь к файлу блокировок берём из конфига плагина, иначе дефолт
  LOCKFILE="$(awk -F= '/^[[:space:]]*locklist/{gsub(/[[:space:]]/,"",$2);print $2}' /etc/dnf/plugins/versionlock.conf 2>/dev/null | head -1)"
  [ -z "$LOCKFILE" ] && LOCKFILE=/etc/dnf/plugins/versionlock.list
  plugin_ok=1
  dnf versionlock --help >/dev/null 2>&1 || plugin_ok=0

  # печать одной записи блокировки как CHANGED (заполняем колонки отчёта)
  print_lock() { echo "CHANGED|$1|$2|$3|$4"; }

  # найти запись блокировки для пакета $1 (сравнение литеральное через case,
  # чтобы имена со спецсимволами regex - gcc-c++ и т.п. - не ломали поиск)
  find_lock() {
    [ -f "$LOCKFILE" ] || return 1
    local e
    while IFS= read -r e; do
      case "$e" in "$1"-[0-9]*|"$1":*) printf '%s\n' "$e"; return 0;; esac
    done < "$LOCKFILE"
    return 1
  }

  # --- просмотр: только читаем файл, ничего не меняем ---
  if [ "$ACTION" = "locklist" ]; then
    filt="$(echo "$PKGS" | tr -s ' ')"
    n=0
    if [ -f "$LOCKFILE" ]; then
      while IFS= read -r e; do
        [ -z "$e" ] && continue
        case "$e" in \#*) continue;; esac    # комментарии плагина пропускаем
        if [ -n "${filt// /}" ]; then
          keep=0
          for p in $filt; do case "$e" in "$p"-[0-9]*|"$p":*) keep=1;; esac; done
          [ "$keep" = "0" ] && continue
        fi
        print_lock "$e" "закреплена" "-" "закрепление"
        n=$((n+1))
      done < "$LOCKFILE"
    fi
    [ "$plugin_ok" = "0" ] && echo "Плагин versionlock не установлен (файл блокировок пуст/отсутствует)"
    echo "Закреплённых версий: $n"
    echo "PKGOP_RESULT: NOTHING"       # просмотр - изменений нет
    echo "REBOOT_RECOMMENDED: no"
    exit 0
  fi

  # --- предпроверка lock/unlock: показываем, что изменится, ничего не пишем ---
  if [ "${DRYRUN:-0}" = "1" ]; then
    echo "=== Предпроверка (dry-run), файл блокировок не меняется ==="
    [ "$plugin_ok" = "0" ] && echo "PKGOP_ERR|плагин versionlock не установлен (при запуске он будет поставлен автоматически: python3-dnf-plugin-versionlock)"
    n=0
    for p in $PKGS; do
      already="$(find_lock "$p")"
      if [ "$ACTION" = "lock" ]; then
        if ! rpm -q "$p" >/dev/null 2>&1; then
          echo "PKGOP_ERR|$p не установлен - нечего закреплять"; continue
        fi
        ver="$(rpm -q --qf '%{VERSION}-%{RELEASE}\n' "$p" 2>/dev/null | tail -1)"
        if [ -n "$already" ]; then
          print_lock "$p" "$ver" "уже закреплён" "без изменений"
        else
          print_lock "$p" "(текущая)" "$ver" "закрепление"; n=$((n+1))
        fi
      else   # unlock
        if [ -n "$already" ]; then
          print_lock "$p" "$already" "(снята)" "снятие"; n=$((n+1))
        else
          print_lock "$p" "не закреплён" "-" "без изменений"
        fi
      fi
    done
    echo "К изменению: $n"
    [ "$n" -eq 0 ] && echo "PKGOP_RESULT: NOTHING" || echo "PKGOP_RESULT: OK"
    echo "REBOOT_RECOMMENDED: no"
    exit 0
  fi

  # --- боевой прогон lock/unlock ---
  if [ "$plugin_ok" = "0" ]; then
    echo "=== Плагин versionlock отсутствует, ставим python3-dnf-plugin-versionlock ==="
    if ! yum -y install python3-dnf-plugin-versionlock; then
      echo "не удалось установить плагин versionlock"
      echo "PKGOP_RESULT: FAIL"; echo "REBOOT_RECOMMENDED: no"; exit 1
    fi
  fi
  bkp="/root/rpu-vlock-$(date +%F_%H%M%S)"; mkdir -p "$bkp"
  cp -a "$LOCKFILE" "$bkp/versionlock.list.before" 2>/dev/null || true
  echo "Бэкап списка блокировок: $bkp (откат: вернуть versionlock.list.before на место)"
  before="$(sort -u "$LOCKFILE" 2>/dev/null)"
  rc=0
  if [ "$ACTION" = "lock" ]; then
    dnf -y versionlock add $PKGS || rc=$?
  else
    dnf -y versionlock delete $PKGS || rc=$?
  fi
  after="$(sort -u "$LOCKFILE" 2>/dev/null)"
  changed=0
  # добавленные записи (закреплены)
  while IFS= read -r e; do
    [ -z "$e" ] && continue; case "$e" in \#*) continue;; esac
    print_lock "$e" "(нет)" "закреплена" "закрепление"; changed=$((changed+1))
  done < <(comm -13 <(printf '%s\n' "$before") <(printf '%s\n' "$after"))
  # удалённые записи (снято закрепление)
  while IFS= read -r e; do
    [ -z "$e" ] && continue; case "$e" in \#*) continue;; esac
    print_lock "$e" "закреплена" "(снята)" "снятие"; changed=$((changed+1))
  done < <(comm -23 <(printf '%s\n' "$before") <(printf '%s\n' "$after"))
  echo "Изменено записей блокировки: $changed"
  # если хоть что-то реально применилось (частичный успех при кривом аргументе) - это OK с предупреждением,
  # а не FAIL: иначе маркер противоречит уже изменённому файлу блокировок.
  if [ "$changed" -gt 0 ]; then
    [ "$rc" -ne 0 ] && echo "Внимание: dnf вернул код $rc - часть аргументов могла не примениться (см. лог выше)"
    echo "PKGOP_RESULT: OK"
  elif [ "$rc" -ne 0 ]; then
    echo "PKGOP_RESULT: FAIL"
  else
    echo "PKGOP_RESULT: NOTHING"
  fi
  echo "REBOOT_RECOMMENDED: no"
  exit 0
fi
# =====================================================================

# --- Режим предпроверки (dry-run): ничего не ставим, только показываем, что уедет ---
if [ "${DRYRUN:-0}" = "1" ]; then
  echo "=== Предпроверка (dry-run), ничего не ставится ==="
  yum clean expire-cache >/dev/null 2>&1 || true
  yum -q makecache >/dev/null 2>&1 || true
  dperr="$(mktemp 2>/dev/null || echo "/tmp/rpu_pkgprev.$$")"
  out="$(yum "$ACTION" $PKGS --assumeno 2>"$dperr")"
  # пакет не найден в репозитории - это не "нечего делать", а ошибка ввода. Показываем.
  nomatch="$(grep -iE 'No match for argument|Unable to find a match|No package .* available|Нет пакета' "$dperr" 2>/dev/null | head -3 | tr '\n' ';' | cut -c1-200)"
  rm -f "$dperr"
  if [ -n "$nomatch" ]; then echo "PKGOP_ERR|$nomatch"; fi
  n=0
  # mode=add - установка/обновление, mode=del - удаление (важно для remove: показываем что снесётся, включая зависимые)
  while IFS=$'\t' read -r mode name ver repo; do
    [ -z "$name" ] && continue
    ver="${ver#*:}"
    if [ "$mode" = "del" ]; then
      echo "CHANGED|$name|$ver|(нет)|удаление"
    else
      if rpm -q "$name" >/dev/null 2>&1; then
        old="$(rpm -q --qf '%{VERSION}-%{RELEASE}\n' "$name" 2>/dev/null | tail -1)"
      else
        old="(нет)"
      fi
      [ -z "$old" ] && old="(нет)"
      echo "CHANGED|$name|$old|$ver|план"
    fi
    n=$((n+1))
  done < <(printf '%s\n' "$out" | awk '
      /^(Installing|Upgrading|Downgrading|Reinstalling|Installing dependencies|Installing weak dependencies|Installing group\/module packages):/ {mode="add"; next}
      /^(Removing|Removing dependent packages|Removing unused dependencies):/ {mode="del"; next}
      /^Transaction Summary/{mode=""}
      /^[[:space:]]*$/{mode=""}
      mode!="" && $2 ~ /^(x86_64|noarch|aarch64|i686|ppc64le|s390x)$/ {print mode"\t"$1"\t"$3"\t"$4}')
  echo "К изменению: $n"
  [ "$n" -eq 0 ] && echo "PKGOP_RESULT: NOTHING" || echo "PKGOP_RESULT: OK"
  echo "REBOOT_RECOMMENDED: no"
  exit 0
fi

# Бэкап списка пакетов для отката (yum history undo <id>)
bkp="/root/rpu-pkgop-$(date +%F_%H%M%S)"; mkdir -p "$bkp"
before="$bkp/rpm-qa.before.txt"; after="$bkp/rpm-qa.after.txt"
rpm -qa --qf '%{NAME} %{VERSION}-%{RELEASE}\n' 2>/dev/null | sort > "$before"
yum history list 2>/dev/null | head -6 > "$bkp/yum-history.before.txt"
echo "Бэкап списка пакетов: $bkp (откат: yum history undo <id>)"

# Метаданные - чтобы видеть свежее зеркало (как в предпроверке/обновлении)
echo "=== Обновляем метаданные ==="
yum clean expire-cache >/dev/null 2>&1 || true
yum -q makecache >/dev/null 2>&1 || true

echo "=== Выполняем: yum -y $ACTION $PKGS ==="
rc=0
yum -y "$ACTION" $PKGS || rc=$?

# Что реально изменилось - диф по СТРОКАМ (имя+версия), а не по имени.
# Иначе installonly-пакеты (kernel-lt держит несколько версий сразу) дают ложные "upgrade".
rpm -qa --qf '%{NAME} %{VERSION}-%{RELEASE}\n' 2>/dev/null | sort > "$after"
changed=0
declare -A ADD REM
# добавленные строки (есть после, нет до) и удалённые (есть до, нет после)
while read -r name ver; do [ -z "$name" ] && continue; ADD[$name]="${ADD[$name]} $ver"; done < <(comm -13 "$before" "$after")
while read -r name ver; do [ -z "$name" ] && continue; REM[$name]="${REM[$name]} $ver"; done < <(comm -23 "$before" "$after")
for name in $(printf '%s\n%s\n' "${!ADD[@]}" "${!REM[@]}" | sort -u); do
  [ -z "$name" ] && continue
  a=($(echo ${ADD[$name]})); r=($(echo ${REM[$name]}))
  if [ "${#a[@]}" -eq 1 ] && [ "${#r[@]}" -eq 1 ]; then
    # одна ушла, одна пришла - это обычный upgrade/downgrade
    newest="$(printf '%s\n%s\n' "${r[0]}" "${a[0]}" | sort -V | tail -1)"
    [ "$newest" = "${a[0]}" ] && act=upgrade || act=downgrade
    echo "CHANGED|$name|${r[0]}|${a[0]}|$act"; changed=$((changed+1))
  else
    # installonly / множественные версии - показываем как есть
    for v in "${a[@]}"; do echo "CHANGED|$name|(нет)|$v|install"; changed=$((changed+1)); done
    for v in "${r[@]}"; do echo "CHANGED|$name|$v|(нет)|erase"; changed=$((changed+1)); done
  fi
done

# Нужен ли reboot по факту (glibc/ядро/systemd и т.п.)
reboot_required=no
if command -v needs-restarting >/dev/null 2>&1; then
  needs-restarting -r >/dev/null 2>&1 || reboot_required=yes
fi

echo "Изменено пакетов: $changed"
if [ "$rc" -ne 0 ]; then
  echo "yum завершился с кодом $rc"
  echo "PKGOP_RESULT: FAIL"
elif [ "$changed" -eq 0 ]; then
  echo "PKGOP_RESULT: NOTHING"
else
  echo "PKGOP_RESULT: OK"
fi
echo "REBOOT_RECOMMENDED: $reboot_required"
