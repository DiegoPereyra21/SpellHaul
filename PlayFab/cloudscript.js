// SpellHaul - CloudScript (Legacy).
// Único camino por el que el menú modifica el perfil del jugador. El loadout y el stash viven en
// Player Data de SOLO LECTURA para el cliente: los escriben este script (menú) y el servidor de
// la run (Server API). Subir en Game Manager > Automation > CloudScript > Revisions (Legacy) y
// desplegar la revisión.
//
// Reglas:
// - EnsureProfile: al loguear. Si el jugador no tiene loadout, le da el kit inicial (Title Data
//   "StarterKit", lo exporta el menú Game/Items/Copy Starter Kit JSON del editor) y un stash vacío.
// - CommitProfile: reacomodar loadout/stash desde el menú. Nunca puede aparecer un item que no
//   estuviera: por cada ItemId, la cantidad y la durabilidad no pueden subir. Con una run en
//   curso no se puede tocar nada. La marca de run en curso la maneja solo el servidor.
// - AbandonActiveRun: dar por perdida la run en curso (el equipo que se llevó se pierde).
//
// Todas devuelven un string JSON: {"ok":true} o {"ok":false,"reason":"..."}.

var LOADOUT_KEY = "PlayerLoadout";
var STASH_KEY = "Stash";
var STARTER_KIT_KEY = "StarterKit";

var STASH_SLOTS = 30;           // StashData.SlotCount
var MAX_EQUIPMENT_SLOTS = 32;   // holgado: EquipmentSlot puede crecer al final
var MAX_POCKET_ENTRIES = 12;    // RunInventory.MaxSnapshotPocketEntries
var MAX_QUANTITY = 9999;
var EPS = 0.0001;

function result(ok, reason) {
    return JSON.stringify({ ok: ok, reason: reason || "" });
}

function parseJson(text) {
    if (!text) return null;
    try { return JSON.parse(text); } catch (e) { return null; }
}

function emptyStack() {
    return { ItemId: "", Quantity: 0, Durability: 0 };
}

function isEmpty(s) {
    return !s || !s.ItemId || !(s.Quantity > 0);
}

function inactiveRun() {
    return { Active: false, Address: "", Port: 0, StartedUnixSeconds: 0 };
}

function emptyStash() {
    var slots = [];
    for (var i = 0; i < STASH_SLOTS; i++) slots.push(emptyStack());
    return { Slots: slots };
}

function readProfile() {
    var r = server.GetUserReadOnlyData({ PlayFabId: currentPlayerId, Keys: [LOADOUT_KEY, STASH_KEY] });
    var data = (r && r.Data) || {};
    return {
        loadout: parseJson(data[LOADOUT_KEY] && data[LOADOUT_KEY].Value),
        stash: parseJson(data[STASH_KEY] && data[STASH_KEY].Value)
    };
}

function writeProfile(loadout, stash) {
    var data = {};
    if (loadout) data[LOADOUT_KEY] = JSON.stringify(loadout);
    if (stash) data[STASH_KEY] = JSON.stringify(stash);
    server.UpdateUserReadOnlyData({ PlayFabId: currentPlayerId, Data: data, Permission: "Private" });
}

function isList(x, max) {
    return Object.prototype.toString.call(x) === "[object Array]" && x.length <= max;
}

function validStack(s) {
    if (isEmpty(s)) return true;
    if (typeof s.ItemId !== "string" || s.ItemId.length > 64) return false;
    if (Math.floor(s.Quantity) !== s.Quantity || s.Quantity < 1 || s.Quantity > MAX_QUANTITY) return false;
    if (typeof s.Durability !== "number" || s.Durability < 0 || s.Durability > 1 + EPS) return false;
    return true;
}

function validStructure(loadout, stash) {
    if (!loadout || !stash) return false;
    if (!isList(loadout.Equipment, MAX_EQUIPMENT_SLOTS)) return false;
    if (!isList(loadout.PocketL, MAX_POCKET_ENTRIES)) return false;
    if (!isList(loadout.PocketR, MAX_POCKET_ENTRIES)) return false;
    if (!isList(stash.Slots, STASH_SLOTS)) return false;
    var lists = [loadout.Equipment, loadout.PocketL, loadout.PocketR, stash.Slots];
    for (var l = 0; l < lists.length; l++)
        for (var i = 0; i < lists[l].length; i++)
            if (!validStack(lists[l][i])) return false;
    return true;
}

// Por ItemId: cantidad total, suma de durabilidad (cantidad x durabilidad) y durabilidad máxima.
function tally(loadout, stash) {
    var t = {};
    var lists = [loadout.Equipment || [], loadout.PocketL || [], loadout.PocketR || [], (stash && stash.Slots) || []];
    for (var l = 0; l < lists.length; l++) {
        for (var i = 0; i < lists[l].length; i++) {
            var s = lists[l][i];
            if (isEmpty(s)) continue;
            var e = t[s.ItemId] || (t[s.ItemId] = { qty: 0, dur: 0, maxDur: 0 });
            var d = typeof s.Durability === "number" ? s.Durability : 0;
            e.qty += s.Quantity;
            e.dur += s.Quantity * d;
            if (d > e.maxDur) e.maxDur = d;
        }
    }
    return t;
}

function conserves(before, after) {
    for (var id in after) {
        if (!after.hasOwnProperty(id)) continue;
        var a = after[id], b = before[id];
        if (!b) return "new_item:" + id;
        if (a.qty > b.qty) return "more_items:" + id;
        if (a.dur > b.dur + EPS || a.maxDur > b.maxDur + EPS) return "durability_up:" + id;
    }
    return null;
}

handlers.EnsureProfile = function (args, context) {
    var cur = readProfile();
    if (cur.loadout) {
        if (!cur.stash) writeProfile(null, emptyStash());
        return result(true);
    }

    var title = server.GetTitleData({ Keys: [STARTER_KIT_KEY] });
    var kit = parseJson(title && title.Data && title.Data[STARTER_KIT_KEY]);
    if (!kit || !isList(kit.Equipment, MAX_EQUIPMENT_SLOTS)) {
        log.error("Falta o es inválido el Title Data '" + STARTER_KIT_KEY + "'.");
        return result(false, "no_starter_kit");
    }

    kit.ActiveRun = inactiveRun();
    writeProfile(kit, cur.stash || emptyStash());
    return result(true);
};

handlers.CommitProfile = function (args, context) {
    var cur = readProfile();
    if (!cur.loadout) return result(false, "no_profile");
    if (cur.loadout.ActiveRun && cur.loadout.ActiveRun.Active) return result(false, "in_run");

    var curStash = cur.stash || emptyStash();
    var newLoadout = args && args.Loadout ? parseJson(args.Loadout) : cur.loadout;
    var newStash = args && args.Stash ? parseJson(args.Stash) : curStash;

    if (!validStructure(newLoadout, newStash)) return result(false, "invalid_structure");

    var problem = conserves(tally(cur.loadout, curStash), tally(newLoadout, newStash));
    if (problem) {
        log.info("CommitProfile rechazado para " + currentPlayerId + ": " + problem);
        return result(false, problem);
    }

    newLoadout.ActiveRun = inactiveRun(); // el cliente no puede marcar ni desmarcar runs
    writeProfile(newLoadout, newStash);
    return result(true);
};

handlers.AbandonActiveRun = function (args, context) {
    var cur = readProfile();
    if (!cur.loadout || !cur.loadout.ActiveRun || !cur.loadout.ActiveRun.Active) return result(true);

    var slots = (cur.loadout.Equipment || []).length;
    var equipment = [];
    for (var i = 0; i < slots; i++) equipment.push(emptyStack());

    writeProfile({ Equipment: equipment, PocketL: [], PocketR: [], ActiveRun: inactiveRun() }, null);
    return result(true);
};
