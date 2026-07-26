using System.Collections.Generic;
using Colossal;

namespace ClearTheWay
{
    public class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Traffic: Clear the Way" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kBehaviorGroup), "Behavior" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Enabled)), "Enable the mod" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Enabled)), "Master switch. Traffic forms an emergency corridor for vehicles running with lights and siren, accidents no longer make queued traffic vanish, and crashed vehicles get recovered properly." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CorridorDistance)), "Corridor distance (m)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CorridorDistance)), "How far ahead of the emergency vehicle cars start pulling aside." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.SqueezePastBlockers)), "Squeeze past stopped cars" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.SqueezePastBlockers)), "Emergency vehicles may carefully pass cars that have pulled aside and stopped, instead of waiting behind them. Disable if you see vehicles clipping through each other too often." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ClearParallelLanes)), "Clear undecided lanes" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ClearParallelLanes)), "On multi-lane roads, also pull cars aside in all lanes the emergency vehicle could still choose." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.OvertakeStuckTraffic)), "Overtake on parallel lanes" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.OvertakeStuckTraffic)), "Stuck emergency vehicles change onto a free neighboring lane to pass the queue, even if they have to merge back later." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ForceGreenLights)), "Green lights for the corridor" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ForceGreenLights)), "Traffic lights along the emergency vehicle's path switch to green, so cars waiting at a red light can clear the way through the crossing." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.UseOncomingLane)), "Use the oncoming lane" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.UseOncomingLane)), "Stuck emergency vehicles cross the center line and pass the queue on the oncoming carriageway when there is a gap in oncoming traffic; without a gap they straddle the center line." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ClearRoundaboutInner)), "Clear the outer lane in roundabouts" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ClearRoundaboutInner)), "In a multi-lane roundabout the corridor rule flips: instead of opening a gap in the middle, every car moves toward the inner lanes and the outermost lane is kept free so the responder can drive around the ring unobstructed. Single-lane roundabouts are unaffected." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.StopPedestrians)), "Pedestrians wait at the kerb" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.StopPedestrians)), "Pedestrians about to step onto a crossing wait while a vehicle with lights and siren approaches, instead of walking out in front of it. Anyone already on the crossing keeps going, so the road clears. Vanilla only ever stops pedestrians at a red light, never for a responder." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AssistTowTrucks)), "Recovery vehicle priority" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AssistTowTrucks)), "Everything that gets a recovery vehicle to the crash: the nearest capable van is sent (or a tow truck from your tow depot if none is near), it drives there directly instead of finishing its road-repair round first, traffic makes way, amber beacons come on, and after 30 seconds stuck it forces its light green and works past the jam." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.UnblockRecoveryVehicles)), "Free wedged recovery vehicles" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.UnblockRecoveryVehicles)), "Last resort: when a recovery vehicle has stood still for minutes on its way to a crash, the single vehicle directly in its way is removed - together with its trailer - so the queue can move again. Only ever that one blocker, never an emergency or recovery vehicle and never a crashed one, and only once the situation is genuinely deadlocked." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.PreventAccidentDespawn)), "No despawn behind accidents" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.PreventAccidentDespawn)), "Vehicles queued behind a crashed vehicle wait for it to be cleared instead of being despawned by the game's stuck-traffic cleanup." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ClearWrecksAside)), "Clear wrecks onto one lane" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ClearWrecksAside)), "A few seconds after a crash, settled wrecks are moved onto the outermost lane of their side of the road. At least one lane stays passable, so traffic queues and squeezes past instead of being evaporated by the game because no route exists past a fully blocked road." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CrossMedians)), "Clear onto tram beds and green strips" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CrossMedians)), "When making way, traffic may also move onto a tram bed, grass strip, median or parking shoulder next to its lane, not just to the edge of its own lane. A tram bed is only used while no tram is on it, and platforms, kerbs and footways are never used. Helps most on narrow roads, where clearing within the lane leaves no gap at all." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TowDepot)), "Tow depot building" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TowDepot)), "Adds the tow depot to the construction menu (a cloned road maintenance depot that ONLY recovers crashed vehicles). Its trucks never do street or snow work, so they head straight to the accident. Takes effect when a game is loaded." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TowWrecks)), "Tow wrecks to the depot" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TowWrecks)), "When a recovery truck stops at a wreck, the wreck is coupled to it and hauled back to the depot - instead of the vanilla behavior where the wreck is 'repaired' and evaporates on the spot." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TowTruckPrefab)), "Dedicated tow truck" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TowTruckPrefab)), "Registers a dedicated tow truck (a cloned road maintenance van restricted to vehicle recovery, with tow-tractor capability) that the tow depot spawns instead of the stock van. With this on, ONLY that tow truck hauls wrecks away; ordinary maintenance vans clear them the vanilla way. Takes effect when a game is loaded." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.VerboseLogging)), "Verbose logging" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.VerboseLogging)), "Write periodic diagnostics to Logs/ClearTheWay.log. Only enable for troubleshooting." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.WatchVehicle)), "Watch vehicle (index)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.WatchVehicle)), "Debugging aid: paste the index of a single vehicle (a veh=/blocker=/car= number from the log) to have its full state dumped a few times a second. Leave empty or 0 to turn off. Requires Verbose logging." },

                { "Assets.NAME[Abschleppfahrzeugdepot]", "Tow depot" },
                { "Assets.DESCRIPTION[Abschleppfahrzeugdepot]", "Dispatches tow trucks that recover crashed vehicles - and nothing else, so they arrive fast. Added by the Clear the Way mod." },
                { "Assets.NAME[Abschleppwagen]", "Tow truck" },
                { "Assets.DESCRIPTION[Abschleppwagen]", "Recovers crashed vehicles and hauls them off - does no street or snow work. Added by the Clear the Way mod." },
            };
        }

        public void Unload()
        {
        }
    }

    public class LocaleDE : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleDE(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Traffic: Clear the Way" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Allgemein" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kBehaviorGroup), "Verhalten" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Enabled)), "Mod aktivieren" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Enabled)), "Hauptschalter. Der Verkehr bildet eine Rettungsgasse für Fahrzeuge mit Sondersignal, Unfälle lassen den gestauten Verkehr nicht mehr verschwinden, und verunfallte Fahrzeuge werden richtig geborgen." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CorridorDistance)), "Gassenlänge (m)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CorridorDistance)), "Ab welcher Entfernung vor dem Einsatzfahrzeug die Fahrzeuge zur Seite fahren." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.SqueezePastBlockers)), "An stehenden Fahrzeugen vorbeiquetschen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.SqueezePastBlockers)), "Einsatzfahrzeuge dürfen vorsichtig an Fahrzeugen vorbeifahren, die zur Seite gefahren sind, statt dahinter zu warten. Deaktivieren, falls Fahrzeuge zu oft ineinander clippen." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ClearParallelLanes)), "Alle möglichen Spuren räumen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ClearParallelLanes)), "Auf mehrspurigen Straßen fahren auch Fahrzeuge auf allen Spuren zur Seite, die das Einsatzfahrzeug noch wählen könnte." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.OvertakeStuckTraffic)), "Auf Nachbarspuren überholen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.OvertakeStuckTraffic)), "Festhängende Einsatzfahrzeuge wechseln auf eine freie Nachbarspur, um die Kolonne zu überholen — auch wenn sie später wieder einfädeln müssen." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ForceGreenLights)), "Grün für die Rettungsgasse" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ForceGreenLights)), "Ampeln entlang des Einsatzwegs schalten auf Grün, damit an roten Ampeln wartende Fahrzeuge die Kreuzung räumen können." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.UseOncomingLane)), "Gegenfahrbahn benutzen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.UseOncomingLane)), "Festhängende Einsatzfahrzeuge überqueren die Mittellinie und überholen die Kolonne auf der Gegenfahrbahn, wenn dort eine Lücke ist; ohne Lücke fahren sie mittig auf der Linie." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ClearRoundaboutInner)), "Im Kreisverkehr die äußere Spur freihalten" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ClearRoundaboutInner)), "In mehrspurigen Kreisverkehren dreht sich die Rettungsgassen-Regel um: Statt in der Mitte eine Lücke zu öffnen, ziehen alle Fahrzeuge nach innen und die äußerste Spur bleibt frei, damit das Einsatzfahrzeug ungehindert um den Kreis fahren kann. Einspurige Kreisverkehre bleiben unberührt." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.StopPedestrians)), "Fußgänger warten am Bordstein" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.StopPedestrians)), "Fußgänger, die gerade einen Überweg betreten wollen, warten, während sich ein Fahrzeug mit Sondersignal nähert, statt ihm vor die Motorhaube zu laufen. Wer schon auf dem Überweg ist, geht weiter und macht die Fahrbahn frei. Vanilla hält Fußgänger nur an roten Ampeln an, nie wegen eines Einsatzfahrzeugs." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AssistTowTrucks)), "Vorrang für Bergungsfahrzeuge" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AssistTowTrucks)), "Alles, was das Bergungsfahrzeug zum Unfall bringt: Der nächstgelegene geeignete Wagen wird geschickt (oder ein Abschleppwagen aus dem Abschleppdepot, wenn keiner in der Nähe ist), er fährt direkt hin statt vorher seine Straßenrunde zu Ende zu fahren, der Verkehr macht Platz, das gelbe Blinklicht geht an, und nach 30 Sekunden Stillstand erzwingt er Grün und arbeitet sich durch den Stau." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.UnblockRecoveryVehicles)), "Festgefahrene Bergungsfahrzeuge befreien" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.UnblockRecoveryVehicles)), "Letztes Mittel: Steht ein Bergungsfahrzeug auf dem Weg zum Unfall minutenlang still, wird das eine Fahrzeug direkt vor ihm entfernt - samt Anhänger -, damit die Kolonne wieder anfährt. Immer nur dieser eine Blockierer, nie ein Einsatz- oder Bergungsfahrzeug und nie ein verunfalltes, und erst wenn die Lage wirklich verfahren ist." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.PreventAccidentDespawn)), "Kein Despawn hinter Unfällen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.PreventAccidentDespawn)), "Fahrzeuge, die sich hinter einem verunfallten Fahrzeug stauen, warten auf dessen Bergung, statt vom Stau-Aufräumsystem des Spiels gelöscht zu werden." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ClearWrecksAside)), "Wracks auf eine Spur räumen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ClearWrecksAside)), "Wenige Sekunden nach einem Unfall werden liegengebliebene Wracks auf die äußerste Spur ihrer Straßenseite geschoben. So bleibt mindestens eine Spur befahrbar — der Verkehr staut sich und quetscht sich vorbei, statt vom Spiel aufgelöst zu werden, weil es an einer voll blockierten Straße keine Route mehr gibt." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CrossMedians)), "Auf Gleisbett und Grünstreifen ausweichen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CrossMedians)), "Beim Platzmachen darf der Verkehr auch auf ein Gleisbett, einen Grünstreifen, einen Mittelstreifen oder einen Parkstreifen neben seiner Spur ausweichen, nicht nur bis an den eigenen Spurrand. Ein Gleisbett wird nur genutzt, solange keine Straßenbahn darauf ist; Bahnsteige, Bordsteine und Gehwege nie. Hilft vor allem auf schmalen Straßen, wo das Ausweichen innerhalb der Spur gar keine Lücke lässt." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TowDepot)), "Abschleppfahrzeugdepot" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TowDepot)), "Fügt das Abschleppfahrzeugdepot ins Baumenü ein (geklontes Straßenwartungsdepot, das AUSSCHLIESSLICH Fahrzeugbergung macht). Seine Fahrzeuge machen nie Straßen- oder Schneearbeiten und fahren deshalb direkt zum Unfall. Wirkt nach dem Laden eines Spielstands." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TowWrecks)), "Wracks zum Depot abschleppen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TowWrecks)), "Hält ein Bergungsfahrzeug am Wrack, wird das Wrack angekuppelt und zum Depot gezogen — statt wie in Vanilla an Ort und Stelle 'repariert' zu werden und sich aufzulösen." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TowTruckPrefab)), "Eigener Abschleppwagen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TowTruckPrefab)), "Registriert einen eigenen Abschleppwagen (geklonter Straßenwartungswagen, nur Fahrzeugbergung, mit Zugfahrzeug-Fähigkeit), den das Depot statt des Standard-Wagens einsetzt. Ist die Option an, schleppt AUSSCHLIESSLICH dieser Abschleppwagen Wracks ab; normale Wartungswagen bergen sie wie in Vanilla. Wirkt nach dem Laden eines Spielstands." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.VerboseLogging)), "Ausführliches Logging" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.VerboseLogging)), "Schreibt regelmäßig Diagnosedaten nach Logs/ClearTheWay.log. Nur zur Fehlersuche aktivieren." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.WatchVehicle)), "Fahrzeug beobachten (Index)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.WatchVehicle)), "Debug-Hilfe: Index eines einzelnen Fahrzeugs eintragen (eine veh=/blocker=/car=-Nummer aus dem Log), dann wird sein voller Zustand ein paar Mal pro Sekunde ins Log geschrieben. Leer oder 0 = aus. Erfordert ausführliches Logging." },

                { "Assets.NAME[Abschleppfahrzeugdepot]", "Abschleppfahrzeugdepot" },
                { "Assets.DESCRIPTION[Abschleppfahrzeugdepot]", "Schickt Abschleppwagen, die verunfallte Fahrzeuge bergen — und sonst nichts tun, deshalb kommen sie schnell. Vom Mod „Clear the Way“ hinzugefügt." },
                { "Assets.NAME[Abschleppwagen]", "Abschleppwagen" },
                { "Assets.DESCRIPTION[Abschleppwagen]", "Birgt verunfallte Fahrzeuge und schleppt sie ab — macht keine Straßen- oder Schneearbeiten. Vom Mod „Clear the Way“ hinzugefügt." },
            };
        }

        public void Unload()
        {
        }
    }
}
