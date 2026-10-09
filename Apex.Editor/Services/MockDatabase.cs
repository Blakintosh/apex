using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Apex.Editor.Models;

namespace Apex.Editor.Services;

/// <summary>
/// Generates a deterministic mock asset database (~3.5k assets across 9 GDTs) with
/// realistic cross-references: weapons point at real xmodels / fx / sounds / materials,
/// and materials point at real images — so the "Referenced by" pane has data to show.
/// </summary>
public static class MockDatabase
{
    public static AssetDatabase Generate()
    {
        var rng = new Random(20260717);
        var gdts = new Dictionary<string, GdtFile>();

        GdtFile Gdt(string name)
        {
            if (!gdts.TryGetValue(name, out var g))
                gdts[name] = g = new GdtFile { Name = name };
            return g;
        }

        AssetRecord Add(string gdt, string type, string name, string? parent = null)
        {
            var rec = new AssetRecord { Name = name, Type = type, GdtName = gdt, Parent = parent };
            Gdt(gdt).Assets.Add(rec);
            return rec;
        }

        string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // ── Images ───────────────────────────────────────────────────────────
        string[] imgSubjects = { "brick", "plaster", "wood_planks", "metal_plate", "concrete", "dirt", "cobble", "roof_tile", "sandbag", "rust", "fabric", "marble", "asphalt", "rock", "moss", "snow", "ice", "bark", "foliage", "glass" };
        string[] imgSuffix = { "_c", "_n", "_g", "_o" };
        var images = new List<AssetRecord>();
        foreach (var s in imgSubjects)
            for (var v = 1; v <= 6; v++)
                foreach (var suf in imgSuffix)
                {
                    var rec = Add("images_env.gdt", "image", $"i_{s}_{v:00}{suf}");
                    rec.Properties["baseImage"] = $"texture_assets/env/{s}_{v:00}{suf}.tif";
                    rec.Properties["semantic"] = suf switch { "_c" => "diffuseMap", "_n" => "normalMap", "_g" => "glossMap", _ => "occlusionMap" };
                    rec.Properties["compression"] = suf == "_n" ? "normal" : "compressed";
                    rec.Properties["streamable"] = "1";
                    images.Add(rec);
                }

        // ── Materials ────────────────────────────────────────────────────────
        var materialSchema = SchemaRegistry.Get("material")!;
        var materials = new List<AssetRecord>();
        foreach (var s in imgSubjects)
            for (var v = 1; v <= 6; v++)
            {
                var rec = Add("materials_env.gdt", "material", $"mtl_{s}_{v:00}");
                FillFromSchema(rec, materialSchema, rng, 0.45);
                rec.Properties["colorMap"] = $"i_{s}_{v:00}_c";
                rec.Properties["normalMap"] = $"i_{s}_{v:00}_n";
                rec.Properties["glossMap"] = $"i_{s}_{v:00}_g";
                if (rng.NextDouble() < 0.5) rec.Properties["occlusionMap"] = $"i_{s}_{v:00}_o";
                materials.Add(rec);
            }

        // ── Environment xmodels ──────────────────────────────────────────────
        var xmodelSchema = SchemaRegistry.Get("xmodel")!;
        var xmodels = new List<AssetRecord>();
        string[] props = { "barrel", "crate", "fence", "chandelier", "door", "rubble", "sandbag_wall", "tree_oak", "rock_granite", "lamp_post", "banner", "statue", "gear_large", "pipe_section", "console", "terminal", "generator", "cot", "tarp", "wire_spool", "girder", "pallet", "bookshelf", "table_round", "chair_wood", "cart", "coffin", "candelabra", "portrait", "clocktower_gear" };
        void EnvModels(string gdt, string prefix, string theme)
        {
            foreach (var p in props)
                for (var v = 1; v <= 4; v++)
                {
                    var rec = Add(gdt, "xmodel", $"{prefix}_{p}_{v:00}");
                    FillFromSchema(rec, xmodelSchema, rng, 0.35);
                    rec.Properties["filename"] = $"{theme}/{p}/{prefix}_{p}_{v:00}.model_bin";
                    xmodels.Add(rec);
                }
        }
        EnvModels("zm_castle_assets.gdt", "zm_castle", "zombie/castle");
        EnvModels("mp_props.gdt", "mp_prop", "mp/props");
        EnvModels("cp_infil_assets.gdt", "cp_infil", "cp/infil");

        // ── FX ───────────────────────────────────────────────────────────────
        string[] fxKinds = { "muzzle_flash", "impact_dirt", "impact_metal", "impact_glass", "explosion_med", "smoke_plume", "spark_burst", "blood_spurt", "electric_arc", "fire_wall", "teleport_swirl", "shell_eject" };
        var fx = new List<AssetRecord>();
        foreach (var k in fxKinds)
            for (var v = 1; v <= 8; v++)
            {
                var rec = Add("fx_common.gdt", "fx", $"fx_{k}_{v:00}");
                rec.Properties["editorFile"] = $"share/raw/fx/common/{k}_{v:00}.efx";
                rec.Properties["looping"] = rng.NextDouble() < 0.25 ? "1" : "0";
                rec.Properties["drawDistance"] = Num(500 + rng.Next(20) * 250);
                rec.Properties["sortOrder"] = rng.Next(0, 12).ToString();
                fx.Add(rec);
            }

        // ── Sounds ───────────────────────────────────────────────────────────
        string[] sndKinds = { "fire", "fire_plr", "reload", "reload_empty", "dryfire", "raise", "putaway", "melee_swipe", "rattle", "adsup", "adsdown" };
        var sounds = new List<AssetRecord>();
        string[] sndWeaponFamilies = { "ar_havoc", "ar_vireo", "ar_kestrel", "smg_wasp", "smg_riot", "smg_needle", "pst_talon", "pst_marshal", "sht_maul", "snp_locus", "snp_ballista", "lmg_dredge", "lmg_bastion", "lnc_spike", "mel_katana", "spc_raygun" };
        foreach (var fam in sndWeaponFamilies)
            foreach (var k in sndKinds)
            {
                var rec = Add("snd_aliases.gdt", "sound", $"wpn_{fam}_{k}");
                rec.Properties["aliasFile"] = $"sound_assets/weapons/{fam}/{k}.wav";
                rec.Properties["volume"] = Num(Math.Round(0.4 + rng.NextDouble() * 0.6, 2));
                rec.Properties["distMax"] = Num(1000 + rng.Next(16) * 250);
                rec.Properties["channel"] = k.Contains("plr") ? "weapon_player" : "weapon_world";
                rec.Properties["bus"] = "BUS_FX";
                sounds.Add(rec);
            }

        // ── Weapons ──────────────────────────────────────────────────────────
        var weaponSchema = SchemaRegistry.Get("weapon")!;
        var weaponDefs = new (string Family, string Display, string Class, string FireType, int Clip, int Damage, double FireTime)[]
        {
            ("ar_havoc",   "KV-7 Havoc",     "rifle",    "Full Auto",     30, 40, 0.107),
            ("ar_vireo",   "Vireo AR-9",     "rifle",    "3-Round Burst", 33, 47, 0.09),
            ("ar_kestrel", "Kestrel MK.2",   "rifle",    "Single Shot",   24, 60, 0.18),
            ("smg_wasp",   "Wasp SMG",       "smg",      "Full Auto",     36, 28, 0.066),
            ("smg_riot",   "Riot-10",        "smg",      "Full Auto",     32, 30, 0.075),
            ("smg_needle", "Needler X4",     "smg",      "4-Round Burst", 48, 24, 0.055),
            ("pst_talon",  "Talon Compact",  "pistol",   "Single Shot",   12, 45, 0.15),
            ("pst_marshal","Marshal .44",    "pistol",   "Single Shot",    6, 95, 0.3),
            ("sht_maul",   "Maul 12ga",      "shotgun",  "Single Shot",    8, 130, 0.5),
            ("snp_locus",  "Locus DSR",      "sniper",   "Single Shot",    5, 198, 1.1),
            ("snp_ballista","Ballista M3",   "sniper",   "Single Shot",    7, 160, 0.85),
            ("lmg_dredge", "Dredge LMG",     "rifle",    "Full Auto",     75, 36, 0.12),
            ("lmg_bastion","Bastion 48",     "rifle",    "Full Auto",     48, 42, 0.13),
            ("lnc_spike",  "Spike Launcher", "launcher", "Single Shot",    1, 350, 1.4),
            ("mel_katana", "Ronin Blade",    "melee",    "Single Shot",    0, 250, 0.6),
            ("spc_raygun", "Ray Gun Mk.I",   "special",  "Single Shot",   20, 180, 0.25),
        };

        var weapons = new List<AssetRecord>();
        foreach (var (family, display, cls, fireType, clip, damage, fireTime) in weaponDefs)
        {
            // View/world models live alongside weapons.
            var viewModel = Add("t7_weapon_models.gdt", "xmodel", $"t7_weapon_{family}_view");
            FillFromSchema(viewModel, xmodelSchema, rng, 0.25);
            viewModel.Properties["filename"] = $"weapons/{family}/viewmodel.model_bin";
            xmodels.Add(viewModel);
            var worldModel = Add("t7_weapon_models.gdt", "xmodel", $"t7_weapon_{family}_world");
            FillFromSchema(worldModel, xmodelSchema, rng, 0.25);
            worldModel.Properties["filename"] = $"weapons/{family}/worldmodel.model_bin";
            xmodels.Add(worldModel);

            foreach (var (suffix, gdt, zombies) in new[] { ("", "t7_weapons.gdt", false), ("_zm", "zm_weapons.gdt", true), ("_zm_upgraded", "zm_weapons.gdt", true) })
            {
                if (cls == "melee" && suffix == "_zm_upgraded") continue;
                var parent = suffix == "_zm_upgraded" ? $"wpn_{family}_zm" : (suffix == "_zm" ? $"wpn_{family}" : null);
                var rec = Add(gdt, "weapon", $"wpn_{family}{suffix}", parent);
                FillFromSchema(rec, weaponSchema, rng, 0.0);

                var upgraded = suffix == "_zm_upgraded";
                rec.Properties["displayName"] = upgraded ? UpgradedName(display) : display;
                rec.Properties["weaponClass"] = cls;
                rec.Properties["fireType"] = fireType;
                rec.Properties["clipSize"] = (upgraded ? clip * 2 : clip).ToString();
                rec.Properties["maxAmmo"] = (clip * (upgraded ? 12 : 8)).ToString();
                rec.Properties["startAmmo"] = (clip * (upgraded ? 6 : 4)).ToString();
                rec.Properties["damage"] = ((int)(damage * (upgraded ? 2.5 : 1))).ToString();
                rec.Properties["minDamage"] = ((int)(damage * (upgraded ? 1.6 : 0.6))).ToString();
                rec.Properties["fireTime"] = Num(fireTime);
                rec.Properties["reloadTime"] = Num(Math.Round(1.4 + rng.NextDouble() * 1.6, 2));
                rec.Properties["reloadEmptyTime"] = Num(Math.Round(1.9 + rng.NextDouble() * 1.8, 2));
                rec.Properties["moveSpeedScale"] = Num(Math.Round(cls switch { "smg" => 1.0, "pistol" => 1.0, "sniper" => 0.88, "launcher" => 0.85, _ => 0.95 } + rng.NextDouble() * 0.04, 2));
                rec.Properties["hipSpread"] = Num(Math.Round(2 + rng.NextDouble() * 4, 1));
                rec.Properties["headShotMultiplier"] = Num(cls == "sniper" ? 2.5 : 1.4);
                rec.Properties["usedInZombies"] = zombies ? "1" : "0";
                rec.Properties["usedInMultiplayer"] = zombies ? "0" : "1";
                rec.Properties["ammoName"] = $"bullet_{cls}";
                rec.Properties["viewModel"] = viewModel.Name;
                rec.Properties["worldModel"] = worldModel.Name;
                rec.Properties["viewFlashEffect"] = fx[rng.Next(8)].Name;                       // muzzle flashes are first
                rec.Properties["worldFlashEffect"] = fx[rng.Next(8)].Name;
                rec.Properties["camoMaterial"] = materials[rng.Next(materials.Count)].Name;
                rec.Properties["fireSound"] = $"wpn_{family}_fire";
                rec.Properties["fireSoundPlayer"] = $"wpn_{family}_fire_plr";
                rec.Properties["reloadSound"] = $"wpn_{family}_reload";
                rec.Properties["emptyFireSound"] = $"wpn_{family}_dryfire";
                if (cls == "sniper") { rec.Properties["rechamberBoltAction"] = "1"; rec.Properties["holdBreathToSteady"] = "1"; }
                if (cls == "shotgun") rec.Properties["segmentedReload"] = "1";
                weapons.Add(rec);
            }
        }

        // ── Attachments ──────────────────────────────────────────────────────
        string[] attachments = { "reflex", "elo", "acog", "suppressor", "extclip", "grip", "stock", "fmj", "laser", "bipod" };
        foreach (var (family, _, cls, _, _, _, _) in weaponDefs)
        {
            if (cls is "melee" or "special" or "launcher") continue;
            foreach (var att in attachments.Take(6 + rng.Next(4)))
            {
                var rec = Add("t7_attachments.gdt", "attachment", $"att_{family}_{att}");
                rec.Properties["attachmentType"] = att;
                rec.Properties["baseWeapon"] = $"wpn_{family}";
                rec.Properties["displayName"] = char.ToUpper(att[0]) + att[1..];
                rec.Properties["adsTimeScale"] = Num(Math.Round(0.85 + rng.NextDouble() * 0.3, 2));
                rec.Properties["damageScale"] = Num(att == "fmj" ? 1.15 : 1);
                rec.Properties["hideTags"] = att == "suppressor" ? "tag_flash" : "";
            }
        }

        // ── Seeded defects: dangling refs and out-of-range values the Problems panel should catch ──
        foreach (var (asset, key, value) in new[]
        {
            ("wpn_smg_wasp", "camoMaterial", "mtl_gold_etched_01"),
            ("wpn_smg_wasp", "hipSpread", "35"),
            ("wpn_pst_talon", "viewFlashEffect", "fx_muzzle_flash_99"),
            ("wpn_snp_locus_zm", "fireSound", "wpn_snp_locus_shoot"),
        })
            weapons.First(w => w.Name == asset).Properties[key] = value;

        // ── Characters & vehicles ────────────────────────────────────────────
        string[] charNames = { "char_marine_assault", "char_marine_heavy", "char_seraph_default", "char_reaper_default", "char_zombie_castle_01", "char_zombie_castle_02", "char_zombie_crawler", "char_sci_lab_01", "char_pilot_f", "char_pilot_m", "char_civilian_01", "char_civilian_02" };
        foreach (var c in charNames)
        {
            var rec = Add("characters_common.gdt", "character", c);
            rec.Properties["model"] = xmodels[rng.Next(xmodels.Count)].Name;
            rec.Properties["voicePack"] = c.Contains("zombie") ? "vo_zombie" : "vo_generic";
            rec.Properties["health"] = (c.Contains("zombie") ? 150 : 100).ToString();
            rec.Properties["team"] = c.Contains("zombie") ? "axis" : "allies";
        }
        string[] vehNames = { "veh_tank_raps", "veh_drone_quad", "veh_gunship", "veh_warthog", "veh_boat_patrol", "veh_mech_reaper", "veh_train_car", "veh_apc_axis" };
        foreach (var v in vehNames)
        {
            var rec = Add("vehicles.gdt", "vehicle", v);
            rec.Properties["model"] = xmodels[rng.Next(xmodels.Count)].Name;
            rec.Properties["health"] = (500 + rng.Next(10) * 250).ToString();
            rec.Properties["maxSpeed"] = Num(20 + rng.Next(12) * 5);
            rec.Properties["physicsPreset"] = "vehicle_heavy";
        }

        // ── Zombies barriers ─────────────────────────────────────────────────
        for (var i = 1; i <= 14; i++)
        {
            var rec = Add("zm_castle_assets.gdt", "zbarrier", $"zm_castle_barrier_{i:00}");
            for (var b = 1; b <= 4; b++)
                rec.Properties[$"boardModel{b}"] = $"zm_castle_fence_{(b % 4) + 1:00}";
            rec.Properties["numBoards"] = "4";
            rec.Properties["repairSound"] = "zmb_board_repair";
        }

        var files = gdts.Values.OrderBy(g => g.Name, StringComparer.Ordinal).ToList();
        foreach (var f in files)
            f.Assets.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        return new AssetDatabase
        {
            Gdts = files,
            Assets = files.SelectMany(f => f.Assets).ToList(),
        };
    }

    private static string UpgradedName(string display) => display switch
    {
        "Ray Gun Mk.I" => "Porter's Ray Gun",
        _ => display + " Ultra",
    };

    /// <summary>Fills a record with schema defaults, randomly deviating a fraction of values so modified-tracking has data.</summary>
    private static void FillFromSchema(AssetRecord rec, AssetSchema schema, Random rng, double deviateChance)
    {
        foreach (var def in schema.Properties)
        {
            var value = def.Default;
            if (rng.NextDouble() < deviateChance)
            {
                value = def.Kind switch
                {
                    PropertyKind.Toggle => def.Default == "1" ? "0" : "1",
                    PropertyKind.Choice when def.Choices.Length > 0 => def.Choices[rng.Next(def.Choices.Length)],
                    PropertyKind.Number when def.HasRange =>
                        Math.Round(def.Min + rng.NextDouble() * (def.Max - def.Min), 2).ToString("0.###", CultureInfo.InvariantCulture),
                    _ => value,
                };
            }
            rec.Properties[def.Key] = value;
        }
    }
}
