# Feature-Plan: Feuerwehr zu Unfällen & Abschleppunternehmen

Recherche im dekompilierten `Game.dll` (v1.6.0) — Stand der Analyse und Plan
für die zwei größeren Wünsche. Der einfach & sicher umsetzbare Teil (Platz
machen für Bergungsfahrzeuge) ist bereits umgesetzt (`AssistTowTrucks`).

## Wichtigste Erkenntnis: Fahrzeugbergung existiert bereits

Das Spiel schleppt verunfallte Fahrzeuge **schon selbst** ab — gezielt, nicht zufällig:

- `DamagedVehicleSystem`: jedes Unfallauto (`Damaged` + `Stopped` + `Car`) erzeugt
  automatisch `MaintenanceRequest(wrack, priority 100)` mit `RequestGroup(32)`.
- Straßenwartungsdepots mit `MaintenanceType.Vehicle` (Flag 8) schicken ein
  Fahrzeug, das das Wrack birgt (`MaintenanceVehicleAISystem`, Zweige bei
  `MaintenanceType.Vehicle`).
- Das „zufällige Vorbeifahren", das du beobachtest, ist die **Straßen-/Schnee-
  wartung im Patrouillenmodus** (`MaintenanceType.Road | Snow`) — ein anderer
  Mechanismus als die Bergung.

→ „Abschleppwagen kontrolliert zum Unfall schicken" ist im Kern also schon da.
Umgesetzt: solche Bergungsfahrzeuge bekommen jetzt eine sanfte Rettungsgasse.

## Feature 1 — Feuerwehr zu Unfällen (auch ohne Brand)

**Dispatch (machbar, mittel):** Request-Muster ist klar — Entity mit Archetype
`[ServiceRequest, FireRescueRequest, RequestGroup]`, `FireRescueRequest(target,
priority, FireRescueRequestType.Fire)` + `RequestGroup(4)`. Ein eigenes System
kann bei neuen `AccidentSite`-Entities mit einstellbarer Wahrscheinlichkeit
einen solchen Request erzeugen (einmal pro Unfall, Dedupe nötig).

**Risiko/offene Frage:** `FireEngineAISystem` sucht am Ziel nach `OnFire` **oder**
`RescueTarget` (Game.Buildings, eigentlich für Gebäudeeinsturz/Katastrophe). Ein
reines Unfallauto hat beides nicht → die Feuerwehr käme an und fände nichts zu
tun (fährt vermutlich wieder weg). Optionen:
  a) Nur „Feuerwehr fährt hin" (Präsenz) — einfach, aber sie tut nichts.
  b) Unfallauto mit `RescueTarget` markieren, damit die Feuerwehr „rettet" —
     muss in-game getestet werden (Verhalten/Nebenwirkungen unklar).

**Verunfallte Fahrzeuge zur Seite auf eine Spur ziehen:** eigenständige Custom-
Logik, unabhängig davon wer da ist. Unfallautos sind `Stopped` (kein `Moving`,
keine Spurbewegung) → müssen per `Transform` versetzt werden. Machbar, aber
fummelig und optisch heikel; sollte gated (default aus) + iterativ getestet
werden.

→ **Empfehlung:** In einem nächsten Schritt mit laufendem Spiel: (1) Dispatch
gated default-aus einbauen, (2) `RescueTarget`-Ansatz live testen, (3) das
„zur Seite ziehen" separat als experimentelle, gated Option.

## Feature 2 — Abschleppwagen + Abschleppunternehmen (neue Prefabs)

- **Platz machen / leicht erhöhte Priorität:** ✅ umgesetzt (`AssistTowTrucks`).
- **Dediziertes Abschleppwagen-Modell + Abschleppunternehmen-Gebäude:** das ist
  Prefab-Erstellung. Realistischer Weg: einen bestehenden `MaintenanceDepot`
  (Straßenwartung) klonen und als eigenes Gebäude registrieren, das nur
  `MaintenanceType.Vehicle` kann; Fahrzeugmodell vorerst = Wartungswagen.
  Das ist ein substanzielles, **nur in-game verifizierbares** Stück (PrefabSystem,
  Registrierung, UI-Icon, Lokalisierung, Platzierbarkeit) und darf nicht blind
  ausgeliefert werden — hohes Crash-/Speicherstand-Risiko ohne Test.

→ **Empfehlung:** Als eigenen, iterativen Arbeitsschritt mit laufendem Spiel
angehen (Prefab klonen → laden → platzieren → Fahrzeug prüfen), nicht ungetestet
in den bestehenden Mod mischen.

## Nächste sinnvolle Reihenfolge

1. `AssistTowTrucks` testen (jetzt möglich).
2. Feuerwehr-Dispatch zu Unfällen, gated default-aus, + Wahrscheinlichkeits-
   Slider. Live testen was die Feuerwehr am Unfall tut.
3. „Unfallautos zur Seite ziehen" als experimentelle Option.
4. Abschleppunternehmen-Gebäude + Abschleppwagen als Prefab-Klon (eigener
   Testzyklus).

## Feature 3 — Anhänger abschleppen statt despawnen (Ziel 0.1.6)

Stand seit 0.1.5: Hänger eines getowten Gespanns werden beim Ankoppeln
**despawnt** (Interimslösung; plus OrphanTrailerSweep für Alt-Waisen).
Für 0.1.6 sollen sie richtig geborgen werden:

- **LKW-Anhänger/Auflieger:** ein **eigenes/neues Zugfahrzeug schicken** —
  nach dem Hookup des Traktors wird der Hänger selbst ein Bergungsziel
  (eigener Request/Dispatch), ein zweiter Abschleppwagen holt ihn.
- **PKW-Anhänger:** über den **regulären Abschleppvorgang** mitnehmen
  (kein separater Truck).

Offene Punkte für die Umsetzung:
- Trailer-Entities sind KEINE `Car`s — alle Tow-Queries/Hookup-Gates sind
  Car-basiert (m_WreckQuery, TowDispatch, TryHookup) → eigene Query/Pfad.
- Drawbar-Follow eines `CarTrailer`-Wracks prüfen (gleiches Teleport-Muster,
  aber Archetype anders: CarTrailerLane statt CarCurrentLane).
- Vanilla `DamagedVehicleSystem` erzeugt MaintenanceRequests nur für
  `Damaged+Stopped+Car` — für den Hänger müssten WIR den Request anlegen
  (oder ihn direkt über TowDispatch einem Van zuweisen).
- Despawn-at-hookup + OrphanTrailerSweep bleiben als Fallback, falls kein
  Truck kommt (Give-up-Prinzip wie bei Wracks).
