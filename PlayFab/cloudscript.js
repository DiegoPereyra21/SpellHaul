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
// - Migración de ItemIds: EnsureProfile reemplaza ids retirados por su equivalente
//   (LEGACY_ITEM_IDS) en el loadout y el stash. Para retirar un item en el futuro, agregarlo ahí.
//
// - Craft / Sell / Buy: economía del hub (crafteo y vendedor). Usan el Title Data "Economy"
//   (lo exporta Game/Economy/Copy Economy JSON) y el oro de la clave de solo lectura "Wallet".
//   Trabajan solo sobre el stash: los ingredientes salen del stash y lo crafteado o comprado entra
//   ahí. El cliente nunca escribe el oro.
//
// Todas devuelven un string JSON: {"ok":true} o {"ok":false,"reason":"..."}.

var LOADOUT_KEY = "PlayerLoadout";
var STASH_KEY = "Stash";
var STARTER_KIT_KEY = "StarterKit";
var WALLET_KEY = "Wallet";
var ECONOMY_KEY = "Economy";

var STASH_SLOTS = 42;           // StashData.SlotCount (antes 30: los stashes viejos se completan)
var MAX_EQUIPMENT_SLOTS = 32;   // holgado: EquipmentSlot puede crecer al final
var MAX_POCKET_ENTRIES = 12;    // RunInventory.MaxSnapshotPocketEntries
var MAX_USABLE_SLOTS = 3;       // ConsumableItemSO.UsableSlotCount (teclas 1-2-3)
var MAX_QUANTITY = 9999;
var EPS = 0.0001;

// ItemId retirado -> ItemId que lo reemplaza. Guantes Swift/Heavy/Focus (stats pasivos) ->
// guantes con habilidad: ofensivos al Orb, Focus al Mending, misma rareza.
var LEGACY_ITEM_IDS = {
    "swift_gloves_common": "orb_gloves_common",
    "swift_gloves_rare": "orb_gloves_rare",
    "swift_gloves_epic": "orb_gloves_epic",
    "heavy_gloves_common": "orb_gloves_common",
    "heavy_gloves_rare": "orb_gloves_rare",
    "heavy_gloves_epic": "orb_gloves_epic",
    "focus_gloves_common": "mending_gloves_common",
    "focus_gloves_rare": "mending_gloves_rare",
    "focus_gloves_epic": "mending_gloves_epic"
};

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
    if (loadout.Usables !== undefined && loadout.Usables !== null && !isList(loadout.Usables, MAX_USABLE_SLOTS)) return false;
    if (!isList(stash.Slots, STASH_SLOTS)) return false;
    var lists = [loadout.Equipment, loadout.PocketL, loadout.PocketR, loadout.Usables || [], stash.Slots];
    for (var l = 0; l < lists.length; l++)
        for (var i = 0; i < lists[l].length; i++)
            if (!validStack(lists[l][i])) return false;
    return true;
}

// Por ItemId: cantidad total, suma de durabilidad (cantidad x durabilidad) y durabilidad máxima.
function tally(loadout, stash) {
    var t = {};
    var lists = [loadout.Equipment || [], loadout.PocketL || [], loadout.PocketR || [], loadout.Usables || [], (stash && stash.Slots) || []];
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

// Reemplaza en el lugar los ItemIds retirados. Devuelve true si cambió algo.
function migrateList(list) {
    var changed = false;
    if (!list) return false;
    for (var i = 0; i < list.length; i++) {
        var s = list[i];
        if (s && s.ItemId && LEGACY_ITEM_IDS.hasOwnProperty(s.ItemId)) {
            s.ItemId = LEGACY_ITEM_IDS[s.ItemId];
            changed = true;
        }
    }
    return changed;
}

function migrateProfile(cur) {
    var loadoutChanged = false, stashChanged = false;
    if (cur.loadout) {
        if (migrateList(cur.loadout.Equipment)) loadoutChanged = true;
        if (migrateList(cur.loadout.PocketL)) loadoutChanged = true;
        if (migrateList(cur.loadout.PocketR)) loadoutChanged = true;
        if (migrateList(cur.loadout.Usables)) loadoutChanged = true;
    }
    if (cur.stash && migrateList(cur.stash.Slots)) stashChanged = true;
    if (loadoutChanged || stashChanged) {
        writeProfile(loadoutChanged ? cur.loadout : null, stashChanged ? cur.stash : null);
        log.info("Perfil de " + currentPlayerId + " migrado (ItemIds retirados).");
    }
}

handlers.EnsureProfile = function (args, context) {
    var cur = readProfile();
    if (cur.loadout) {
        migrateProfile(cur);
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
    // Billetera inicial (si no existe todavía).
    var w = server.GetUserReadOnlyData({ PlayFabId: currentPlayerId, Keys: [WALLET_KEY] });
    if (!(w && w.Data && w.Data[WALLET_KEY]))
        server.UpdateUserReadOnlyData({ PlayFabId: currentPlayerId, Data: { "Wallet": JSON.stringify({ Gold: 0 }) }, Permission: "Private" });
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

    var usables = [];
    for (var u = 0; u < MAX_USABLE_SLOTS; u++) usables.push(emptyStack());
    writeProfile({ Equipment: equipment, PocketL: [], PocketR: [], Usables: usables, ActiveRun: inactiveRun() }, null);
    return result(true);
};

// ---------- Economía (crafteo y vendedor) ----------

function readWallet() {
    var r = server.GetUserReadOnlyData({ PlayFabId: currentPlayerId, Keys: [WALLET_KEY] });
    var w = parseJson(r && r.Data && r.Data[WALLET_KEY] && r.Data[WALLET_KEY].Value);
    var gold = w && typeof w.Gold === "number" ? Math.floor(w.Gold) : 0;
    return gold < 0 ? 0 : gold;
}

// Una sola escritura para stash y oro: o se aplican los dos o ninguno.
function writeStashAndWallet(stash, gold) {
    var data = {};
    data[STASH_KEY] = JSON.stringify(stash);
    data[WALLET_KEY] = JSON.stringify({ Gold: gold });
    server.UpdateUserReadOnlyData({ PlayFabId: currentPlayerId, Data: data, Permission: "Private" });
}

// Title Data "Economy" con mapas por id para buscar rápido.
function readEconomy() {
    var t = server.GetTitleData({ Keys: [ECONOMY_KEY] });
    var e = parseJson(t && t.Data && t.Data[ECONOMY_KEY]);
    if (!e) return null;
    var econ = { sellRate: typeof e.SellRate === "number" ? e.SellRate : 0.4, items: {}, recipes: {}, offers: {} };
    var i;
    for (i = 0; i < (e.Items || []).length; i++) econ.items[e.Items[i].ItemId] = e.Items[i];
    for (i = 0; i < (e.Recipes || []).length; i++) econ.recipes[e.Recipes[i].Id] = e.Recipes[i];
    for (i = 0; i < (e.Offers || []).length; i++) econ.offers[e.Offers[i].Id] = e.Offers[i];
    return econ;
}

function maxStackOf(econ, itemId) {
    var it = econ.items[itemId];
    return it && it.MaxStack > 0 ? it.MaxStack : 1;
}

// Cantidad total de un item en el stash.
function countInStash(stash, itemId) {
    var n = 0;
    for (var i = 0; i < stash.Slots.length; i++) {
        var s = stash.Slots[i];
        if (!isEmpty(s) && s.ItemId === itemId) n += s.Quantity;
    }
    return n;
}

// Saca 'qty' unidades del item del stash (desde las pilas más chicas). Devuelve la durabilidad
// máxima de lo sacado (para que un guante mejorado conserve el estado del original).
function takeFromStash(stash, itemId, qty) {
    var slots = [];
    for (var i = 0; i < stash.Slots.length; i++)
        if (!isEmpty(stash.Slots[i]) && stash.Slots[i].ItemId === itemId) slots.push(i);
    slots.sort(function (a, b) { return stash.Slots[a].Quantity - stash.Slots[b].Quantity; });

    var maxDur = 0;
    for (var k = 0; k < slots.length && qty > 0; k++) {
        var s = stash.Slots[slots[k]];
        var take = Math.min(s.Quantity, qty);
        if (s.Durability > maxDur) maxDur = s.Durability;
        s.Quantity -= take;
        qty -= take;
        if (s.Quantity <= 0) stash.Slots[slots[k]] = emptyStack();
    }
    return maxDur;
}

// Mete 'qty' del item en el stash (apila primero). False si no entra entero.
function addToStash(stash, econ, itemId, qty, durability) {
    var max = maxStackOf(econ, itemId);
    var i, s;
    if (max > 1) {
        for (i = 0; i < stash.Slots.length && qty > 0; i++) {
            s = stash.Slots[i];
            if (isEmpty(s) || s.ItemId !== itemId) continue;
            var add = Math.min(max - s.Quantity, qty);
            if (add <= 0) continue;
            s.Quantity += add;
            qty -= add;
        }
    }
    for (i = 0; i < stash.Slots.length && qty > 0; i++) {
        if (!isEmpty(stash.Slots[i])) continue;
        var put = Math.min(max, qty);
        stash.Slots[i] = { ItemId: itemId, Quantity: put, Durability: durability };
        qty -= put;
    }
    return qty <= 0;
}

// Estado común: perfil, stash, oro y economía. Error → string con el motivo.
function loadEconomyState() {
    var cur = readProfile();
    if (!cur.loadout) return "no_profile";
    if (cur.loadout.ActiveRun && cur.loadout.ActiveRun.Active) return "in_run";
    var econ = readEconomy();
    if (!econ) {
        log.error("Falta o es inválido el Title Data '" + ECONOMY_KEY + "'.");
        return "no_economy";
    }
    var stash = cur.stash || emptyStash();
    while (stash.Slots.length < STASH_SLOTS) stash.Slots.push(emptyStack());
    return { stash: stash, gold: readWallet(), econ: econ };
}

function positiveInt(x, fallback, max) {
    var n = parseInt(x, 10);
    if (isNaN(n) || n < 1) return fallback;
    return Math.min(n, max);
}

handlers.Craft = function (args, context) {
    var st = loadEconomyState();
    if (typeof st === "string") return result(false, st);

    var recipe = st.econ.recipes[args && args.RecipeId];
    if (!recipe) return result(false, "unknown_recipe");
    if (st.gold < recipe.Gold) return result(false, "not_enough_gold");

    var i;
    for (i = 0; i < recipe.Inputs.length; i++)
        if (countInStash(st.stash, recipe.Inputs[i].ItemId) < recipe.Inputs[i].Quantity)
            return result(false, "missing_materials");

    var durability = 1;
    for (i = 0; i < recipe.Inputs.length; i++) {
        var taken = takeFromStash(st.stash, recipe.Inputs[i].ItemId, recipe.Inputs[i].Quantity);
        // Mejorar un equipo (ej. guante Common → Rare) conserva su durabilidad.
        if (recipe.Inputs[i].Quantity === 1 && maxStackOf(st.econ, recipe.Inputs[i].ItemId) === 1) durability = taken;
    }
    if (!addToStash(st.stash, st.econ, recipe.Output, recipe.OutputQuantity, durability))
        return result(false, "stash_full");

    writeStashAndWallet(st.stash, st.gold - recipe.Gold);
    return result(true);
};

handlers.Sell = function (args, context) {
    var st = loadEconomyState();
    if (typeof st === "string") return result(false, st);

    var index = parseInt(args && args.SlotIndex, 10);
    if (isNaN(index) || index < 0 || index >= st.stash.Slots.length) return result(false, "invalid_slot");
    var s = st.stash.Slots[index];
    if (isEmpty(s)) return result(false, "empty_slot");
    if (args && args.ItemId && args.ItemId !== s.ItemId) return result(false, "slot_changed");

    var item = st.econ.items[s.ItemId];
    if (!item) return result(false, "unknown_item");
    var unit = Math.max(1, Math.floor(item.Value * st.econ.sellRate));
    var qty = positiveInt(args && args.Quantity, s.Quantity, s.Quantity);

    s.Quantity -= qty;
    if (s.Quantity <= 0) st.stash.Slots[index] = emptyStack();
    writeStashAndWallet(st.stash, st.gold + unit * qty);
    return result(true);
};

handlers.Buy = function (args, context) {
    var st = loadEconomyState();
    if (typeof st === "string") return result(false, st);

    var offer = st.econ.offers[args && args.OfferId];
    if (!offer) return result(false, "unknown_offer");
    var times = positiveInt(args && args.Count, 1, 20);
    var cost = offer.Price * times;
    if (st.gold < cost) return result(false, "not_enough_gold");
    if (!addToStash(st.stash, st.econ, offer.ItemId, offer.Quantity * times, 1)) return result(false, "stash_full");

    writeStashAndWallet(st.stash, st.gold - cost);
    return result(true);
};
