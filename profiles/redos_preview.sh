#!/bin/bash
# Предпроверка: dry-run РЕАЛЬНОЙ транзакции выбранного профиля (ничего не ставит и не перезагружает).
# Считает транзакцию через dnf --assumeno и классифицирует каждый пакет.
# Вход (env):
#   PROFILE : kernel_security | security_only | kernel_only
#   EXCLUDE : маски пакетов через пробел (как в боевом прогоне), напр. "postgresql*"
# Маркеры для парсера (7-е поле - "причина"/reason, заполняется только для dep):
#   PKG|sec|name|old|new|repo|            - обновится по security-advisory
#   PKG|dep|name|old|new|repo|reason      - зависимость/сопутствующее; reason = кто тянет
#   PKG|kern|name|old|new|repo|           - ядро
#   PKG|excl|name|old|new|repo|           - под маской исключения, БУДЕТ ПРОПУЩЕН (в транзакцию не входит)
#   PREVIEW_DONE|total|sec|dep|excluded
#
# Классификация по advisory берётся из "dnf updateinfo list --security".
# LC_ALL=C - чтобы dnf печатал английские заголовки секций (Installing/Upgrading), иначе парсинг ломается на локали.

export LC_ALL=C
export COLUMNS=200   # не даём dnf переносить длинные NEVRA - иначе парсер теряет строки
set -f   # маски (postgresql*) не должны разворачиваться в имена файлов из CWD; глоббинг по путям тут не используется

PROFILE="${PROFILE:-kernel_security}"
# ${EXCLUDE-...} (без ":") - если переменная ЗАДАНА пустой из GUI (исключения сняты), уважаем пустую; дефолт только когда не задана вовсе
EXCLUDE="${EXCLUDE-postgresql*}"

exargs=""
for m in $EXCLUDE; do exargs="$exargs --exclude=$m"; done

echo "=== Предпроверка транзакции (dry-run, ничего не ставится) ==="
echo "Хост: $(hostname 2>/dev/null)  Ядро: $(uname -r)"
echo "Профиль: $PROFILE   Исключения: ${EXCLUDE:-(нет)}"
# ОС узла - машиночитаемый маркер (парсится в GUI). Нужен, чтобы видеть состав парка (RED OS 7.3 / 8 / др.)
# без ручной разметки: логика профиля писалась и проверялась на 7.3, на новых версиях поведение --security /
# needs-restarting / versionlock не подтверждено - эта строка даёт след для диагностики, если что-то поедет не так.
osname="$( (. /etc/os-release 2>/dev/null; echo "$PRETTY_NAME") 2>/dev/null)"
[ -z "$osname" ] && osname="неизвестно"
osver="$( (. /etc/os-release 2>/dev/null; echo "$VERSION_ID") 2>/dev/null)"
case "$osver" in
  7.3|8.0) ;;
  *) echo "PREVIEW_ERR|RED OS ${osver:-unknown} пока не поддерживается"; exit 1;;
esac
command -v dnf >/dev/null 2>&1 || { echo "PREVIEW_ERR|DNF не найден"; exit 1; }
dnfver="$(dnf --version 2>/dev/null | head -1 | tr -d '\n')"
echo "OS_INFO|$osname|$(uname -r)|${dnfver:-?}"
echo

# stderr транзакции - в errf (для детекта "репозиторий недоступен"); stderr makecache - отдельно в mcerr,
# чтобы мигнувшее при рефреше зеркало не помечало ошибкой узел, у которого транзакция посчиталась из кеша.
errf="$(mktemp 2>/dev/null || echo "/tmp/rpu_prev_err.$$")"
mcerr="$(mktemp 2>/dev/null || echo "/tmp/rpu_prev_mc.$$")"
: > "$errf"; : > "$mcerr"

# 0) ФОРСИМ обновление метаданных. Иначе на устаревшем кеше dnf покажет "0", хотя в зеркале уже есть новее -
#    непропатченный узел выглядел бы как чистый. Так "0 в транзакции" становится честным на всех узлах.
echo "Обновляю метаданные репозиториев (dnf makecache --refresh)..."
dnf -q clean expire-cache >>"$mcerr" 2>&1 || true
dnf -q makecache --refresh >>"$mcerr" 2>&1 || true

# 1) Набор пакетов, у которых есть security-advisory (последний столбец - NEVRA.arch).
# Код возврата dnf проверяем отдельно (не через pipe в awk): если updateinfo/--security на этой версии ОС
# не работает (неизвестная опция, нет плагина, битые метаданные) - молчаливая пустота выглядела бы как
# "0 security-обновлений", хотя это могла быть ошибка команды. Не роняем предпроверку целиком (total/dep
# всё ещё считаются верно) - только явно предупреждаем, что деление на sec/dep может быть неточным.
secraw="$(dnf -q updateinfo list --security 2>>"$errf")"; secrc=$?
if [ "$secrc" -ne 0 ]; then
  echo "ВНИМАНИЕ: dnf updateinfo list --security завершился с кодом $secrc - деление на 'по advisory'/'зависимость' ниже может быть неточным на этой версии ОС"
fi
secinfo="$(printf '%s\n' "$secraw" | awk '{print $NF}')"

# 2) Реальная транзакция выбранного профиля через --assumeno (dnf сам отвечает "нет", ничего не ставит)
run_tx() { dnf update "$@" --assumeno 2>>"$errf"; }

case "$PROFILE" in
  security_only)
    txout="$(run_tx --security $exargs)"
    ;;
  kernel_only)
    txout="$(run_tx kernel-lt kernel-lt-tools kernel-lt-tools-libs)"
    ;;
  *) # kernel_security: считаем обе транзакции и объединяем (дедуп по имени ниже)
    txout="$(run_tx kernel-lt kernel-lt-tools kernel-lt-tools-libs)"$'\n'"$(run_tx --security $exargs)"
    ;;
esac

# Парсер таблицы транзакции dnf: из секций действий берём имя/новую версию/репозиторий
parse_tx() {
  # Строку пакета опознаём по АРХ во втором поле. Без TTY dnf переносит длинные NEVRA на след. строку -
  # привязка к арх-полю отсекает строки-продолжения и не теряет пакеты с длинными именами.
  awk '
    /^(Installing|Upgrading|Downgrading|Reinstalling|Installing dependencies|Installing weak dependencies|Installing group\/module packages):/ { inb=1; next }
    /^(Removing|Transaction Summary|Remove)/ { inb=0 }
    /^[[:space:]]*$/ { inb=0 }
    inb==1 && $2 ~ /^(x86_64|noarch|aarch64|i686|ppc64le|s390x)$/ {
      print $1"\t"$3"\t"$4;
    }
  '
}

# Разбираем транзакцию во временный файл (дедуп по имени) - нужен двойной проход:
# сначала знать ВЕСЬ состав, чтобы для зависимостей понять, кто их тянет из этой же транзакции.
txf="$(mktemp 2>/dev/null || echo "/tmp/rpu_prev_tx.$$")"
printf '%s\n' "$txout" | parse_tx | awk -F'\t' '!s[$1]++' > "$txf"
TXNAMES=" $(cut -f1 "$txf" | tr '\n' ' ')"   # имена всех пакетов транзакции (для поиска требующих)

total=0; secn=0; depn=0

while IFS=$'\t' read -r name new repo; do
  [ -z "$name" ] && continue

  new="${new#*:}"   # убрать epoch "N:"
  # текущая версия: если пакет не установлен (новая установка) - rpm -q пишет "... is not installed" в stdout, не берём это как версию
  if rpm -q "$name" >/dev/null 2>&1; then
    old="$(rpm -q --qf '%{VERSION}-%{RELEASE}\n' "$name" 2>/dev/null | tail -1)"
  else
    old="(новый)"
  fi
  [ -z "$old" ] && old="(новый)"

  cat="dep"
  case "$name" in kernel*) cat="kern";; esac
  if [ "$cat" = "dep" ]; then
    esc="$(printf '%s' "$name" | sed 's/[.+*]/\\&/g')"
    if printf '%s\n' "$secinfo" | grep -qE "(^|[ /])${esc}-[0-9]"; then cat="sec"; fi
  fi

  # Для зависимости выясняем, кто её тянет: сначала требующие из этой же транзакции, иначе - вообще требующие пакеты, иначе слабая зависимость.
  reason=""
  if [ "$cat" = "dep" ]; then
    reqs="$(dnf -q repoquery --whatrequires "$name" --qf '%{name}\n' 2>/dev/null | sort -u)"
    inreq=""
    for rq in $reqs; do case "$TXNAMES" in *" $rq "*) inreq="$inreq $rq";; esac; done
    inreq="$(echo $inreq | xargs 2>/dev/null)"
    if [ -n "$inreq" ]; then
      reason="для: $inreq"
    elif [ -n "$reqs" ]; then
      reason="нужен пакетам: $(echo $reqs | tr ' ' '\n' | head -3 | tr '\n' ' ' | xargs) ..."
    else
      reason="слабая зависимость (Recommends/группа)"
    fi
  fi

  echo "PKG|$cat|$name|$old|$new|$repo|$reason"
  case "$cat" in
    sec) secn=$((secn+1));;
    dep) depn=$((depn+1));;
  esac
  total=$((total+1))
done < "$txf"
rm -f "$txf"

# 3) Что подходит под маску исключения (апдейт есть, но в транзакцию не пойдёт) - помечаем "будет пропущен"
excluded=0
for m in $EXCLUDE; do
  while read -r nm nv rp; do
    [ -z "$nm" ] && continue
    base="${nm%.*}"   # name.arch -> name
    old="$(rpm -q --qf '%{VERSION}-%{RELEASE}\n' "$base" 2>/dev/null | tail -1)"
    [ -z "$old" ] && old="(нет)"
    echo "PKG|excl|$base|$old|$nv|$rp|"
    excluded=$((excluded+1))
  done < <(dnf -q check-update "$m" 2>/dev/null | awk 'NF>=3 && $1 ~ /\./{print $1" "$2" "$3}')
done

# 5) Если dnf ругался на репозиторий/метаданные - это НЕ "нечего обновлять", а ошибка. Отдаём маркер.
#    Ошибки транзакции (errf) - всегда. Ошибки makecache (mcerr) - только если пакетов не нашлось вообще
#    (иначе транзакция явно посчиталась из валидного кеша, мигнувший рефреш не важен).
ERRPAT='Errors during downloading metadata|Failed to download|Cannot (download|prepare)|No more mirrors|Curl error|Could not resolve|Connection (refused|timed out)|Failed to synchronize cache|Status code: [45][0-9][0-9]|repolist: 0'
reperr="$(grep -iE "$ERRPAT" "$errf" 2>/dev/null | head -1 | tr -d '\r' | cut -c1-200)"
if [ -z "$reperr" ] && [ "$total" -eq 0 ]; then
  reperr="$(grep -iE "$ERRPAT" "$mcerr" 2>/dev/null | head -1 | tr -d '\r' | cut -c1-200)"
fi
rm -f "$errf" "$mcerr"
if [ -n "$reperr" ]; then
  echo "PREVIEW_ERR|проблема с репозиторием/dnf: $reperr"
fi

echo
echo "Итого в транзакции: $total (по advisory: $secn, зависимости: $depn), исключено маской: $excluded"
echo "PREVIEW_DONE|$total|$secn|$depn|$excluded"
