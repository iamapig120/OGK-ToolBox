using OGKToolBox.Core.Models;

namespace OGKToolBox.Tests;

public sealed class CharacterExpressionRulesTests
{
    [Theory]
    [InlineData("Chara_00100611_Face_A_00", true)]
    [InlineData("chara_00100611_face_F_00", true)]
    [InlineData("UI_SLC_Story_Face_Shadow", false)]
    [InlineData("Chara_00100611_Face_Shadow", false)]
    [InlineData("Chara_00100611_Base_00", false)]
    public void OnlyCharacterFaceSpritesAreSelectable(string name, bool expected) =>
        Assert.Equal(expected, CharacterExpressionRules.IsFaceLayer(name));

    [Theory]
    [InlineData("anm_chara_00100001", 1000, true)]
    [InlineData("ANM_CHARA_00100011", 1000, true)]
    [InlineData("anm_chara_00100001_aura_00", 1000, true)]
    [InlineData("anm_chara_00100101", 1001, true)]
    [InlineData("ui_card_chara_001000", 0, false)]
    public void ModelIdsCanBeReadFromCharacterBundleNames(string key, int expectedModelId, bool expectedResult)
    {
        Assert.Equal(expectedResult, CharacterExpressionRules.TryGetModelIdFromBundle(key, out var modelId));
        Assert.Equal(expectedModelId, modelId);
    }
}
