namespace BotRandomizer;

internal sealed class CosmeticRoller
{
    private const int MaximumStickers = 5;
    private const int CleanCraftThreshold = 35;
    private const int SingleStickerCraftThreshold = 47;
    private const int PairCraftThreshold = 55;
    private const int ThreeStickerCraftThreshold = 63;
    private const int FourStickerCraftThreshold = 88;
    private const int StickerSlabDefinition = 37;
    private const int MinimumKeychainSeed = 1;
    private const int MaximumKeychainSeed = 100000;
    private const int KeychainChanceNumerator = 7;
    private const int KeychainChanceDenominator = 10;

    private readonly Random _random;
    private readonly CosmeticCatalog _catalog;
    private readonly CharmPlacementCatalog _charmPlacements;
    private readonly WeaponWearAllocator _wearAllocator = new();

    internal CosmeticRoller(
        CosmeticCatalog catalog,
        CharmPlacementCatalog charmPlacements,
        Random? random = null)
    {
        _catalog = catalog;
        _charmPlacements = charmPlacements;
        _random = random ?? new Random();
    }

    internal BotCosmeticLoadout RollLoadout(byte team, int? preservedMusicKit = null)
    {
        var agentPool = team == RandomizerAssets.CounterTerroristTeam
            ? RandomizerAssets.CounterTerroristAgents
            : RandomizerAssets.TerroristAgents;
        var agent = Pick(agentPool);
        var (knife, glove) = RollOutfit();

        return new BotCosmeticLoadout
        {
            Team = team,
            AgentModel = agent.ModelPath,
            AgentDefIndex = agent.DefIndex,
            MusicKit = preservedMusicKit ?? Pick(_catalog.MusicKits),
            Knife = knife,
            Glove = glove
        };
    }

    internal WeaponCosmeticSelection? GetOrCreateWeapon(BotCosmeticLoadout loadout, ushort defIndex)
    {
        if (loadout.Weapons.TryGetValue(defIndex, out var existing))
            return existing;
        if (!_catalog.TryGetWeapon(defIndex, out var weapon) || weapon.Paints.Count == 0)
            return null;

        var paint = PickWeaponPaint(weapon.Paints);
        var stickers = RollStickers(paint.Legacy
            ? weapon.LegacyStickerSchemaCount
            : weapon.StickerSchemaCount);
        var keychain = RollKeychain(defIndex);
        var wear = _wearAllocator.Reserve(defIndex, paint, stickers);
        var selection = new WeaponCosmeticSelection(
            paint.PaintKit,
            0,
            wear,
            paint.Legacy,
            stickers,
            keychain);
        loadout.Weapons.Add(defIndex, selection);
        return selection;
    }

    internal void ResetMap() => _wearAllocator.Reset();

    private (KnifeSelection Knife, GloveSelection Glove) RollOutfit()
    {
        var knifeDefinition = PickWeighted(
            RandomizerAssets.Knives,
            knife => knife.Weight);
        if (!_catalog.TryGetKnifePaints(knifeDefinition.DefIndex, out var knifePaints))
        {
            throw new InvalidOperationException(
                $"No paint catalog for knife {knifeDefinition.DefIndex}.");
        }
        var knifePaint = PickKnifePaint(knifePaints);
        var glove = PickWeighted(
            _catalog.Gloves,
            gloveVariant => RandomizerAssets.GetGloveVariantWeight(gloveVariant.DefIndex));
        return (
            new KnifeSelection(
                knifeDefinition.DefIndex,
                knifePaint.PaintKit,
                DefaultWear(knifePaint.WearMin, knifePaint.WearMax)),
            new GloveSelection(
                glove.DefIndex,
                glove.PaintKit,
                DefaultWear(glove.WearMin, glove.WearMax)));
    }

    private IReadOnlyList<StickerSelection> RollStickers(int schemaCount)
    {
        if (_catalog.StickerKits.Count == 0 || schemaCount <= 0)
            return Array.Empty<StickerSelection>();

        var craftRoll = _random.Next(100);
        if (craftRoll < CleanCraftThreshold)
            return Array.Empty<StickerSelection>();

        var category = PickStickerCategory();
        var count = Math.Min(
            craftRoll < SingleStickerCraftThreshold
                ? 1
                : craftRoll < PairCraftThreshold
                    ? 2
                    : craftRoll < ThreeStickerCraftThreshold
                        ? 3
                        : craftRoll < FourStickerCraftThreshold
                            ? 4
                            : 5,
            schemaCount);
        var repeated = count == 1 || _random.Next(100) < GetRepeatChance(count);
        if (!repeated && category.Count < count)
            category = PickStickerCategory(count);
        var craftPool = count == 4
            ? PickFourStickerFinishPool(category, repeated)
            : category;
        if (repeated)
        {
            return RepeatSticker(
                PickSticker(craftPool),
                count);
        }

        var selections = new StickerSelection[count];
        var used = new HashSet<uint>();
        for (var slot = 0; slot < count; slot++)
        {
            var candidates = craftPool
                .Where(sticker => !used.Contains(sticker.DefIndex))
                .ToArray();
            var sticker = PickSticker(candidates.Length > 0 ? candidates : craftPool);
            used.Add(sticker.DefIndex);
            selections[slot] = CreateSticker(sticker, slot);
        }
        return selections;
    }

    private IReadOnlyList<StickerSelection> RepeatSticker(
        StickerCatalogEntry sticker,
        int count)
    {
        var selections = new StickerSelection[count];
        for (var slot = 0; slot < count; slot++)
            selections[slot] = CreateSticker(sticker, slot);
        return selections;
    }

    private static StickerSelection CreateSticker(
        StickerCatalogEntry sticker,
        int slot)
        => new(sticker.DefIndex, slot, (uint)slot);

    private IReadOnlyList<StickerCatalogEntry> PickStickerCategory(int minimumCount = 1)
        => PickWeighted(
            _catalog.StickerCategoryPools
                .Where(category => category.Count >= minimumCount)
                .ToArray(),
            category => Math.Max(1, (int)Math.Round(Math.Sqrt(category.Count))));

    private StickerCatalogEntry PickSticker(IReadOnlyList<StickerCatalogEntry> stickers)
    {
        var finishPools = stickers
            .GroupBy(sticker => sticker.Finish)
            .Select(group => (IReadOnlyList<StickerCatalogEntry>)group.ToArray())
            .ToArray();
        var finishPool = PickWeighted(
            finishPools,
            pool => RandomizerAssets.GetStickerFinishWeight(pool[0].Finish));
        return Pick(finishPool);
    }

    private IReadOnlyList<StickerCatalogEntry> PickFourStickerFinishPool(
        IReadOnlyList<StickerCatalogEntry> stickers,
        bool repeated)
    {
        var finishPools = stickers
            .GroupBy(sticker => sticker.Finish)
            .Select(group => (IReadOnlyList<StickerCatalogEntry>)group.ToArray())
            .ToArray();
        var eligiblePools = repeated
            ? finishPools
            : finishPools.Where(pool => pool.Count >= 4).ToArray();
        return PickWeighted(
            eligiblePools.Length > 0 ? eligiblePools : finishPools,
            pool => repeated
                ? RandomizerAssets.GetFourRepeatStickerFinishWeight(pool[0].Finish)
                : RandomizerAssets.GetFourMixedStickerFinishWeight(pool[0].Finish));
    }

    private static int GetRepeatChance(int count)
        => count switch
        {
            2 => 25,
            3 => 28,
            4 => 41,
            5 => 21,
            _ => 100
        };

    private KeychainSelection? RollKeychain(ushort weaponDefIndex)
    {
        if (_catalog.KeychainDefinitions.Count == 0
            || _random.Next(KeychainChanceDenominator) >= KeychainChanceNumerator)
            return null;

        var definition = Pick(_catalog.KeychainDefinitions);
        var sticker = definition == StickerSlabDefinition
            ? PickSticker(PickStickerCategory()).DefIndex
            : (uint?)null;
        var placement = _charmPlacements.TryGetPlacements(weaponDefIndex, out var placements)
            ? Pick(placements)
            : (CharmPlacement?)null;
        return new KeychainSelection(
            definition,
            _random.Next(MinimumKeychainSeed, MaximumKeychainSeed + 1),
            Sticker: sticker,
            X: placement?.X,
            Y: placement?.Y,
            Z: placement?.Z);
    }

    private PaintCatalogEntry PickWeaponPaint(IReadOnlyList<PaintCatalogEntry> paints)
    {
        var rarityPools = paints
            .GroupBy(paint => paint.Rarity)
            .Select(group => (IReadOnlyList<PaintCatalogEntry>)group.ToArray())
            .ToArray();
        var rarityPool = PickWeighted(
            rarityPools,
            pool => RandomizerAssets.GetWeaponRarityWeight(pool[0].Rarity));
        return Pick(rarityPool);
    }

    private KnifePaintCatalogEntry PickKnifePaint(
        IReadOnlyList<KnifePaintCatalogEntry> paints)
    {
        var finishPools = paints
            .GroupBy(paint => paint.Finish, StringComparer.Ordinal)
            .Select(group => (IReadOnlyList<KnifePaintCatalogEntry>)group.ToArray())
            .ToArray();
        var finishPool = PickWeighted(
            finishPools,
            pool => _catalog.GetKnifeFinishWeight(pool[0].Finish));
        return Pick(finishPool);
    }

    private T Pick<T>(IReadOnlyList<T> values)
    {
        if (values.Count == 0)
            throw new InvalidOperationException("Cannot roll from an empty cosmetic pool.");
        return values[_random.Next(values.Count)];
    }

    private T PickWeighted<T>(IReadOnlyList<T> values, Func<T, int> getWeight)
    {
        var totalWeight = 0;
        foreach (var value in values)
        {
            var weight = getWeight(value);
            if (weight <= 0)
                throw new InvalidOperationException("Cosmetic weights must be positive.");
            totalWeight = checked(totalWeight + weight);
        }

        if (totalWeight == 0)
            throw new InvalidOperationException("Cannot roll from an empty cosmetic pool.");

        var roll = _random.Next(totalWeight);
        foreach (var value in values)
        {
            var weight = getWeight(value);
            if (roll < weight)
                return value;
            roll -= weight;
        }

        throw new InvalidOperationException("Weighted cosmetic selection did not resolve.");
    }

    private static float DefaultWear(float minimum, float maximum)
        => Math.Clamp(0.01f, minimum, maximum);
}
