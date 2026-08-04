# Feature-Plan

Vorausschauender Plan, Stand **0.1.12**. Was ausgeliefert ist, steht hier nur als
Einzeiler, damit es niemand erneut plant — Details dazu in der Git-Historie und in
`PublishConfiguration.xml`. Ausführlich sind nur die **offenen** Punkte und die
**Sackgassen**, damit letztere nicht ein zweites Mal angelaufen werden.

## Erledigt

Der ursprüngliche Plan (Stand 0.1.5) drehte sich um Bergung und Abschleppen. Das ist
inzwischen weitgehend umgesetzt, jeweils mit eigener Option:

| Was | Option |
|---|---|
| Rettungsgasse für Bergungsfahrzeuge | `AssistTowTrucks` |
| Festsitzende Bergungsfahrzeuge freibekommen | `UnblockRecoveryVehicles` |
| Verkehr hinter einem Unfall nicht despawnen lassen | `PreventAccidentDespawn` |
| Wracks auf die äußere Spur räumen | `ClearWrecksAside` |
| Abschleppdepot als eigenes Gebäude | `TowDepot` |
| Wracks wirklich wegschleppen | `TowWrecks` |
| Dedizierter Abschleppwagen als Prefab-Klon | `TowTruckPrefab` |

Die damalige Kernerkenntnis gilt weiter und erklärt, warum das Depot nötig war:
das Spiel birgt Unfallwagen **schon selbst** über `DamagedVehicleSystem` →
`MaintenanceRequest(prio 100)` → Depot mit `MaintenanceType.Vehicle`. Das
„zufällige Vorbeifahren" ist die Straßen-/Schneewartung im Patrouillenmodus, ein
anderer Mechanismus. Das eigene Depot kann ausschließlich `MaintenanceType.Vehicle`
und schickt seine Fahrzeuge deshalb direkt zum Unfall.

## Offen — Feuerwehr zu Unfällen (auch ohne Brand)

**Nicht gebaut.** Im Code existiert kein `FireRescueRequest`, kein `RescueTarget`
und kein eigener Dispatch; Feuerwehrfahrzeuge werden bisher nur als Einsatzfahrzeuge
*erkannt*, nicht zu Unfällen *geschickt*.

Das Request-Muster ist klar: Entity mit Archetype
`[ServiceRequest, FireRescueRequest, RequestGroup]`, dazu
`FireRescueRequest(target, priority, FireRescueRequestType.Fire)` + `RequestGroup(4)`.
Ein eigenes System kann bei neuen `AccidentSite`-Entities mit einstellbarer
Wahrscheinlichkeit einen solchen Request erzeugen — einmal pro Unfall, Dedupe nötig.

**Der Haken:** `FireEngineAISystem` sucht am Ziel nach `OnFire` **oder**
`RescueTarget` (Game.Buildings, gedacht für Gebäudeeinsturz). Ein reines Unfallauto
hat beides nicht, die Feuerwehr käme also an und fände nichts zu tun. Zwei Wege:

- **a)** Nur Präsenz — einfach, aber sie steht nur herum.
- **b)** Unfallauto mit `RescueTarget` markieren, damit sie „rettet". Nebenwirkungen
  unklar, nur in-game prüfbar.

Wenn das angegangen wird: gated und default aus, wie bei allem anderen auch.

## Sackgasse — Anhänger eines Gespanns richtig bergen

**Zweimal versucht, beide Male harter Burst-Crash.** Es bleibt bei der
Interimslösung aus 0.1.5: der Anhänger eines geschleppten Gespanns **despawnt beim
Ankoppeln**, dazu räumt ein Orphan-Sweep Alt-Waisen ab.

Warum es scheitert: die Vanilla-Trailer-Maschinerie liest Traktor- und Trailer-Daten
über `PrefabRef` mit **ungeschützten Lookups**. `CarTrailerMoveSystem` greift auf
`CarData` + `CarTrailerData` zu, die ein gewöhnliches Auto schlicht nicht hat — ein
Wrack als echten Trailer einzuhängen ist damit ein Nullpointer im Burst-Job. Ein
`LayoutElement`-Buffer, der auf ein gelöschtes Mitglied zeigt, ist dieselbe Klasse
von Absturz.

- Der **Flatbed-Pfad** ist retired, liegt aber intakt in `TowFlatbed.cs`.
- Der **wreck-as-trailer-Umbau** liegt auf Branch `wreck-as-trailer-B` und stolpert
  weiterhin über `CarTrailerMoveSystem`.
- Was stattdessen läuft: ein statischer Teleport-Follow hinter dem Truck
  („Deichsel"), ohne jede Vanilla-Trailer-Maschinerie — deshalb absturzfrei.

**Vor einem dritten Anlauf** muss der ungeschützte Prefab-Lookup gelöst sein, nicht
umgangen. Sonst ist es dieselbe Sackgasse mit anderem Anstrich.

## Nächste sinnvolle Reihenfolge

1. Feuerwehr-Dispatch zu Unfällen, gated default-aus, mit Wahrscheinlichkeits-Slider —
   und live prüfen, was die Feuerwehr am Unfall überhaupt tut.
2. Anhängerbergung nur dann erneut, wenn es für den Prefab-Lookup eine echte Lösung gibt.

Laufende Robustheits- und Bugthemen (Gegenverkehr-Pass, Crosswalk-Guard) gehören
nicht hierher — dieser Plan sammelt Features.
