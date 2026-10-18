# Meteor Magnet — гравитационный захват метеоритов для The Planet Crafter

Мод на BepInEx 5 + Harmony. Добавляет постройку **«Гравитационный захват»** (Meteor Magnet).

## Что делает

1. Устройство ставится как обычная постройка (категория «Хранилища», внутри — инвентарь большого контейнера).
2. Мод следит за всеми летящими метеоритами. Если метеорит с ресурсами должен упасть в радиусе захвата
   (по умолчанию 400 м по горизонтали), его «захватывает» ближайшее устройство, у которого не забит инвентарь.
3. Захваченный метеорит плавно разворачивается и падает рядом с устройством (на расстоянии `ImpactOffset`),
   никого не повреждая и не оставляя на земле ни ресурсов, ни кратера.
4. Когда метеорит падает, все его ресурсы сразу оказываются в инвентаре устройства. Внизу экрана появляется сообщение.

Метеориты без ресурсов (обычные камни, дождь, грозы) мод не трогает. Если все устройства забиты,
метеориты падают как обычно. В мультиплеере вся логика работает на хосте.

## Установка

1. Установи [BepInEx 5 (x64)](https://github.com/BepInEx/BepInEx/releases) в папку игры и запусти игру один раз.
2. Скопируй `MeteorMagnet.dll` и `MeteorMagnet.png` в `The Planet Crafter\BepInEx\plugins\MeteorMagnet\`.

## Сборка

Нужен .NET SDK 6+ и установленная игра (сборке нужны DLL игры из `Planet Crafter_Data\Managed`).

```
cd MeteorMagnet
dotnet build -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\The Planet Crafter"
```

Если в папке игры уже стоит BepInEx, после сборки DLL и иконка сами скопируются в `BepInEx\plugins\MeteorMagnet`.

## Настройки

Файл `BepInEx\config\satan007.theplanetcrafter.meteormagnet.cfg` появится после первого запуска.

| Секция | Параметр | По умолчанию | Описание |
|---|---|---|---|
| General | Enabled | true | Включить мод |
| General | DebugLog | false | Подробный лог (полезно для отладки) |
| Capture | Radius | 400 | Радиус захвата, м |
| Capture | Everywhere | false | Ловить все метеориты на планете |
| Capture | LootMultiplier | 1 | Множитель лута |
| Capture | MinLootPerMeteor | 1 | Минимум предметов с метеорита |
| Capture | ShowNotification | true | Сообщение при захвате |
| Visual | PullMeteors | true | Притягивать метеорит к устройству |
| Visual | PullTurnRate | 90 | Скорость доворота, °/с |
| Visual | ImpactOffset | 25 | На каком расстоянии от устройства он упадёт |
| Visual | LeaveAsteroidBody | false | Оставлять тело метеорита/кратер |
| Visual | ShakeOnImpact | false | Тряска камеры при падении |
| Building | Recipe | `Alloy,Alloy,Titanium,Titanium,Iron,Iron,Rod-uranium` | Рецепт (id через запятую) |
| Building | UnlockAtTerraformation | 0 | С какого Ti открывается (0 = сразу) |

## Как устроено

- `Plugin.cs` — регистрирует постройку: клонирует `Container2` в `StaticDataHandler.LoadStaticData`,
  задаёт рецепт, иконку, перевод (RU/EN) и разблокировку.
- `MagnetController.cs` — каждые 0.25 с ищет объекты `Asteroid`, предсказывает точку падения, выбирает
  устройство, обнуляет у метеорита `numberOfResourceInDebris`/`doDamage`/`placeAsteroidBody`, поворачивает его
  к устройству и, когда `hasCrashed` становится `true`, кладёт ресурсы (`groupsSelected`) в инвентарь.

## Известные ограничения / что проверить в игре

Мод пока не проверялся в игре. Имена классов и полей игры взяты из открытых модов
(akarnokd/ThePlanetCrafterMods, aedenthorn/PlanetCrafterMods), но некоторые вещи стоит проверить:

- **Количество лута.** В данных игры `numberOfResourceInDebris` похоже на долю обломков-ресурсов.
  Мод считает `debrisNumber × numberOfResourceInDebris`. Если лута окажется меньше или больше, чем в ванилле,
  подстрой `LootMultiplier` (включи `DebugLog`, в логе будут исходные значения).
- **Притягивание.** Если метеорит двигается не по `transform.forward` и не через `Rigidbody`, он упадёт на прежнее
  место, но ресурсы всё равно придут в устройство.
- Устройство пока выглядит как фиолетово подсвеченный большой контейнер, своей модели у него нет.
