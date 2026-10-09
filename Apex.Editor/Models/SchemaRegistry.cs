using System;
using System.Collections.Generic;

namespace Apex.Editor.Models;

/// <summary>
/// Editing schemas per asset type. In mock mode these are the three hand-coded builders below; in
/// live mode <see cref="Populate"/> swaps in the full set parsed from the BO3 deffiles
/// (see <c>Services/Gdf/GdfSchemaLoader</c>). Types without a schema fall back to a generic
/// key/value editor.
/// </summary>
public static class SchemaRegistry
{
    private static readonly Dictionary<string, AssetSchema> MockSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon"] = BuildWeapon(),
        ["xmodel"] = BuildXModel(),
        ["material"] = BuildMaterial(),
    };

    /// <summary>The active schema set. Starts as the hand-coded mock builders; replaced in live mode.</summary>
    private static IReadOnlyDictionary<string, AssetSchema> _active = MockSchemas;

    /// <summary>
    /// Replaces the active schema set with schemas parsed from real deffiles (live mode). Idempotent;
    /// a null or empty map is ignored so the app keeps whatever it had. Case-insensitive lookups are
    /// preserved (the loader already returns an OrdinalIgnoreCase map).
    /// </summary>
    public static void Populate(IReadOnlyDictionary<string, AssetSchema>? schemas)
    {
        if (schemas is null || schemas.Count == 0)
            return;
        _active = schemas;
    }

    /// <summary>Restores the hand-coded mock schemas (used by tests / mock-mode fallback).</summary>
    public static void ResetToMock() => _active = MockSchemas;

    public static AssetSchema? Get(string typeName) => _active.GetValueOrDefault(typeName);

    /// <summary>Every type with a schema.</summary>
    public static IEnumerable<string> TypeNames => _active.Keys;

    public static string GdfName(string typeName) =>
        _active.TryGetValue(typeName, out var s) ? s.GdfName : typeName + ".gdf";

    private static AssetSchema BuildWeapon() => new()
    {
        TypeName = "weapon",
        GdfName = "weapon.gdf",
        Properties = new List<PropertyDef>
        {
            // ── Identity ─────────────────────────────────────────────
            new("displayName", "Display Name", "Identity", PropertyKind.Text,
                "Localized name shown in the HUD and loot tables."),
            new("weaponClass", "Weapon Class", "Identity", PropertyKind.Choice,
                "Broad handling class used by animation and AI systems.")
                { Choices = new[] { "rifle", "smg", "pistol", "shotgun", "sniper", "launcher", "melee", "special" }, Default = "rifle" },
            new("inventoryType", "Inventory Slot", "Identity", PropertyKind.Choice,
                "Which inventory slot the weapon occupies.")
                { Choices = new[] { "primary", "offhand", "item", "altmode" }, Default = "primary" },
            new("usedInCampaign", "Campaign", "Identity", PropertyKind.Toggle,
                "Weapon is available in Campaign session mode.") { Default = "1" },
            new("usedInMultiplayer", "Multiplayer", "Identity", PropertyKind.Toggle,
                "Weapon is available in Multiplayer session mode.") { Default = "1" },
            new("usedInZombies", "Zombies", "Identity", PropertyKind.Toggle,
                "Weapon is available in Zombies session mode.") { Default = "0" },

            // ── Ammunition ───────────────────────────────────────────
            new("clipSize", "Magazine Size", "Ammunition", PropertyKind.Number,
                "Rounds held in a single magazine.") { Min = 1, Max = 200, Step = 1, Default = "30" },
            new("maxAmmo", "Max Reserve Ammo", "Ammunition", PropertyKind.Number,
                "Maximum rounds carried in reserve.") { Min = 0, Max = 999, Step = 1, Default = "240" },
            new("startAmmo", "Starting Ammo", "Ammunition", PropertyKind.Number,
                "Reserve ammo granted when the weapon is first picked up.") { Min = 0, Max = 999, Step = 1, Default = "120" },
            new("ammoName", "Ammo Pool", "Ammunition", PropertyKind.Text,
                "Shared ammo pool identifier. Weapons with the same pool share reserve ammo.") { Default = "bullet_rifle" },
            new("reloadTime", "Reload Time", "Ammunition", PropertyKind.Number,
                "Seconds for a tactical (non-empty) reload.") { Min = 0.2, Max = 10, Step = 0.05, Default = "2.1" },
            new("reloadEmptyTime", "Reload Time (Empty)", "Ammunition", PropertyKind.Number,
                "Seconds for a reload from an empty magazine.") { Min = 0.2, Max = 12, Step = 0.05, Default = "2.8" },
            new("segmentedReload", "Segmented Reload", "Ammunition", PropertyKind.Toggle,
                "Reload one round at a time (shotgun style) and allow interrupting.") { Default = "0" },

            // ── Damage & Range ───────────────────────────────────────
            new("damage", "Damage", "Damage & Range", PropertyKind.Number,
                "Base damage per bullet inside max-damage range.") { Min = 1, Max = 500, Step = 1, Default = "40" },
            new("minDamage", "Min Damage", "Damage & Range", PropertyKind.Number,
                "Damage per bullet at or beyond min-damage range.") { Min = 1, Max = 500, Step = 1, Default = "25" },
            new("meleeDamage", "Melee Damage", "Damage & Range", PropertyKind.Number,
                "Damage dealt by a melee lunge with this weapon.") { Min = 0, Max = 500, Step = 5, Default = "150" },
            new("headShotMultiplier", "Headshot Multiplier", "Damage & Range", PropertyKind.Number,
                "Damage multiplier applied on headshots.") { Min = 0.5, Max = 5, Step = 0.1, Default = "1.4" },
            new("maxDamageRange", "Max Damage Range", "Damage & Range", PropertyKind.Number,
                "Distance (inches) up to which base damage applies.") { Min = 0, Max = 20000, Step = 50, Default = "1500" },
            new("minDamageRange", "Min Damage Range", "Damage & Range", PropertyKind.Number,
                "Distance (inches) beyond which min damage applies.") { Min = 0, Max = 30000, Step = 50, Default = "2800" },
            new("penetrateType", "Penetration", "Damage & Range", PropertyKind.Choice,
                "How well bullets punch through geometry.")
                { Choices = new[] { "none", "small", "medium", "large" }, Default = "medium" },
            new("impactType", "Impact Type", "Damage & Range", PropertyKind.Choice,
                "Surface impact effect family used on bullet hits.")
                { Choices = new[] { "bullet_small", "bullet_large", "bullet_ap", "shotgun", "grenade_bounce", "projectile" }, Default = "bullet_small" },

            // ── Handling ─────────────────────────────────────────────
            new("fireType", "Fire Mode", "Handling", PropertyKind.Choice,
                "Trigger behaviour of the weapon.")
                { Choices = new[] { "Full Auto", "Single Shot", "2-Round Burst", "3-Round Burst", "4-Round Burst" }, Default = "Full Auto" },
            new("fireTime", "Fire Time", "Handling", PropertyKind.Number,
                "Seconds between shots. 0.06 ≈ 1000 RPM.") { Min = 0.03, Max = 2, Step = 0.005, Default = "0.12" },
            new("adsTransInTime", "ADS In Time", "Handling", PropertyKind.Number,
                "Seconds to enter aim-down-sights.") { Min = 0.05, Max = 2, Step = 0.01, Default = "0.25" },
            new("adsTransOutTime", "ADS Out Time", "Handling", PropertyKind.Number,
                "Seconds to leave aim-down-sights.") { Min = 0.05, Max = 2, Step = 0.01, Default = "0.2" },
            new("hipSpread", "Hipfire Spread", "Handling", PropertyKind.Number,
                "Base bullet spread in degrees when firing from the hip.") { Min = 0, Max = 20, Step = 0.1, Default = "4" },
            new("adsSpread", "ADS Spread", "Handling", PropertyKind.Number,
                "Bullet spread in degrees when aiming down sights.") { Min = 0, Max = 10, Step = 0.05, Default = "0" },
            new("moveSpeedScale", "Move Speed Scale", "Handling", PropertyKind.Number,
                "Player movement speed multiplier while holding this weapon.") { Min = 0.4, Max = 1.5, Step = 0.01, Default = "0.95" },
            new("sprintOutTime", "Sprint-Out Time", "Handling", PropertyKind.Number,
                "Seconds before the weapon can fire after sprinting.") { Min = 0, Max = 1.5, Step = 0.01, Default = "0.25" },
            new("rechamberBoltAction", "Bolt Action", "Handling", PropertyKind.Toggle,
                "Weapon rechambers manually between shots.") { Default = "0" },
            new("holdBreathToSteady", "Hold Breath", "Handling", PropertyKind.Toggle,
                "Player can hold breath to steady the scope.") { Default = "0" },
            new("isDualWield", "Dual Wield", "Handling", PropertyKind.Toggle,
                "Weapon is held akimbo in both hands.") { Default = "0" },

            // ── Models & FX ──────────────────────────────────────────
            new("viewModel", "View Model", "Models & FX", PropertyKind.AssetRef,
                "First-person xmodel rendered in the player's hands.") { RefType = "xmodel" },
            new("worldModel", "World Model", "Models & FX", PropertyKind.AssetRef,
                "Third-person / dropped-weapon xmodel.") { RefType = "xmodel" },
            new("viewFlashEffect", "View Muzzle Flash", "Models & FX", PropertyKind.AssetRef,
                "First-person muzzle flash effect.") { RefType = "fx" },
            new("worldFlashEffect", "World Muzzle Flash", "Models & FX", PropertyKind.AssetRef,
                "Third-person muzzle flash effect.") { RefType = "fx" },
            new("tracerType", "Tracer", "Models & FX", PropertyKind.Choice,
                "Tracer visual used for fired bullets.")
                { Choices = new[] { "none", "standard", "wide", "plasma", "explosive" }, Default = "standard" },
            new("camoMaterial", "Base Camo", "Models & FX", PropertyKind.AssetRef,
                "Default camo material applied to the weapon surfaces.") { RefType = "material" },

            // ── Audio ────────────────────────────────────────────────
            new("fireSound", "Fire Sound", "Audio", PropertyKind.AssetRef,
                "Sound alias played per shot (world).") { RefType = "sound" },
            new("fireSoundPlayer", "Fire Sound (Player)", "Audio", PropertyKind.AssetRef,
                "Sound alias played per shot for the local player.") { RefType = "sound" },
            new("reloadSound", "Reload Sound", "Audio", PropertyKind.AssetRef,
                "Sound alias for the reload animation.") { RefType = "sound" },
            new("emptyFireSound", "Dry-Fire Sound", "Audio", PropertyKind.AssetRef,
                "Sound alias when firing with an empty magazine.") { RefType = "sound" },
        },
    };

    private static AssetSchema BuildXModel() => new()
    {
        TypeName = "xmodel",
        GdfName = "xmodel.gdf",
        Properties = new List<PropertyDef>
        {
            new("filename", "Source File", "Source", PropertyKind.Text,
                "Path to the exported model file, relative to model_export."),
            new("scale", "Scale", "Source", PropertyKind.Number,
                "Uniform scale applied at conversion time.") { Min = 0.01, Max = 10, Step = 0.01, Default = "1" },
            new("skinOverride", "Skin Override", "Source", PropertyKind.Text,
                "Optional material remap list applied at convert time.") { TextEditor = PropertyTextEditor.Lines },
            new("physicsPreset", "Physics Preset", "Collision", PropertyKind.Choice,
                "Physics preset used for dynamic simulation.")
                { Choices = new[] { "default", "none", "wood", "metal", "glass", "cloth", "rubble" }, Default = "default" },
            new("bulletCollisionLOD", "Bullet Collision", "Collision", PropertyKind.Choice,
                "LOD used for bullet collision tests.")
                { Choices = new[] { "None", "High", "Medium", "Low" }, Default = "High" },
            new("playerCollision", "Player Collision", "Collision", PropertyKind.Toggle,
                "Players collide with this model.") { Default = "1" },
            new("castsShadow", "Casts Shadow", "Collision", PropertyKind.Toggle,
                "Model casts dynamic shadows.") { Default = "1" },
            new("lodMedium", "Medium LOD", "LODs", PropertyKind.Text,
                "Exported model used for the medium level of detail."),
            new("lodLow", "Low LOD", "LODs", PropertyKind.Text,
                "Exported model used for the low level of detail."),
            new("lodBias", "LOD Bias", "LODs", PropertyKind.Number,
                "Distance bias applied when selecting LODs.") { Min = -10, Max = 10, Step = 0.1, Default = "0" },
        },
    };

    private static AssetSchema BuildMaterial() => new()
    {
        TypeName = "material",
        GdfName = "material.gdf",
        Properties = new List<PropertyDef>
        {
            new("colorMap", "Color Map", "Maps", PropertyKind.AssetRef,
                "Albedo texture.") { RefType = "image" },
            new("normalMap", "Normal Map", "Maps", PropertyKind.AssetRef,
                "Tangent-space normal map.") { RefType = "image" },
            new("glossMap", "Gloss Map", "Maps", PropertyKind.AssetRef,
                "Gloss / roughness texture.") { RefType = "image" },
            new("occlusionMap", "Occlusion Map", "Maps", PropertyKind.AssetRef,
                "Baked ambient occlusion texture.") { RefType = "image" },
            new("materialType", "Material Type", "Surface", PropertyKind.Choice,
                "Shading model family.")
                { Choices = new[] { "lit", "lit_alphatest", "unlit", "effect", "decal", "sky" }, Default = "lit" },
            new("surfaceType", "Surface Type", "Surface", PropertyKind.Choice,
                "Physical surface response — footsteps, impacts, decals.")
                { Choices = new[] { "dirt", "metal", "wood", "glass", "concrete", "flesh", "snow", "water" }, Default = "concrete" },
            new("blendFunc", "Blend Mode", "Surface", PropertyKind.Choice,
                "How the material blends with the framebuffer.")
                { Choices = new[] { "opaque", "blend", "add", "multiply", "screen" }, Default = "opaque" },
            new("uvScale", "UV Scale", "Tiling", PropertyKind.Number,
                "Texture coordinate scale.") { Min = 0.05, Max = 16, Step = 0.05, Default = "1" },
            new("clampU", "Clamp U", "Tiling", PropertyKind.Toggle,
                "Clamp instead of tile along U.") { Default = "0" },
            new("clampV", "Clamp V", "Tiling", PropertyKind.Toggle,
                "Clamp instead of tile along V.") { Default = "0" },
        },
    };
}
