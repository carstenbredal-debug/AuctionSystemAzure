namespace AuctionSystem.Functions.Models;

public class BoxRow
{
    public int BoxNumber { get; set; }
    public string BoxType { get; set; } = "";
    public string SalesType { get; set; } = "";
    public string Gender { get; set; } = "";
    public string Group { get; set; } = "";
    public string Damages { get; set; } = "";
    public string Size { get; set; } = "";
    public string HairLength { get; set; } = "";
    public string Color { get; set; } = "";
    public string Quality { get; set; } = "";
    public string Clarity { get; set; } = "";
    public int Skins { get; set; }
}

public class GeneratedLotDto
{
    public Guid UniqueID { get; set; } = Guid.NewGuid();
    public string IsShow { get; set; } = "";
    public int? ShowlotBoxNumber { get; set; }
    public string SalesType { get; set; } = "";
    public string Group { get; set; } = "";
    public string Gender { get; set; } = "";
    public string Size { get; set; } = "";
    public string Color { get; set; } = "";
    public string Quality { get; set; } = "";
    public string Clarity { get; set; } = "";
    public string HairLength { get; set; } = "";
    public string Damages { get; set; } = "";
    public string IncludedBoxNumbers { get; set; } = "";
    public int BoxCount { get; set; }
    public int TotalSkins { get; set; }
}

public class SkippedGroupDto
{
    public Guid RunID { get; set; }
    public string Reason { get; set; } = "";
    public string SalesType { get; set; } = "";
    public string Group { get; set; } = "";
    public string Gender { get; set; } = "";
    public string Size { get; set; } = "";
    public string Color { get; set; } = "";
    public string Quality { get; set; } = "";
    public string Clarity { get; set; } = "";
    public string HairLength { get; set; } = "";
    public string Damages { get; set; } = "";
    public int BoxCount { get; set; }
    public int ShowlotCount { get; set; }
    public int TotalSkins { get; set; }
    public string BoxNumbers { get; set; } = "";
}

public class LotGenerationResult
{
    public Guid RunId { get; set; }
    public List<GeneratedLotDto> Lots { get; set; } = new();
    public List<SkippedGroupDto> SkippedGroups { get; set; } = new();
}

public class CatalogBuildResult
{
    public List<CatalogLotDto> CatalogLots { get; set; } = new();
    public List<SkippedGroupDto> SkippedGroups { get; set; } = new();
}

public class CatalogLotDto
{
    public Guid LotUniqueID { get; set; }
    public int StringNumber { get; set; }
    public int LotNumber { get; set; }
    public int CatalogSortOrder { get; set; }
    public string IsShow { get; set; } = "";
    public string SalesType { get; set; } = "";
    public string Gender { get; set; } = "";
    public string Group { get; set; } = "";
    public string HairLength { get; set; } = "";
    public string Size { get; set; } = "";
    public string Quality { get; set; } = "";
    public string Color { get; set; } = "";
    public string Clarity { get; set; } = "";
    public string Damages { get; set; } = "";
    public string IncludedBoxNumbers { get; set; } = "";
    public int BoxCount { get; set; }
    public int TotalSkins { get; set; }
}

public class CatalogPdfRow
{
    public int CatalogSortOrder { get; set; }
    public int StringNumber { get; set; }
    public int LotNumber { get; set; }
    public string IsShow { get; set; } = "";
    public string SalesType { get; set; } = "";
    public string Gender { get; set; } = "";
    public string Group { get; set; } = "";
    public string HairLength { get; set; } = "";
    public string Size { get; set; } = "";
    public string Quality { get; set; } = "";
    public string Color { get; set; } = "";
    public string Clarity { get; set; } = "";
    public string Damages { get; set; } = "";
    public string IncludedBoxNumbers { get; set; } = "";
    public int BoxCount { get; set; }
    public int TotalSkins { get; set; }
    public int LotsInString { get; set; }
    public int LotSequenceInString { get; set; }
    public int StringTotalSkins { get; set; }
    public int StringBoxCount { get; set; }

    // Auctioneer ("Auc") PDF only — null on the customer catalogue.
    public string? Estimate { get; set; }
    public string? Remarks { get; set; }

    public bool IsLastLotInString => LotSequenceInString == LotsInString;
    public bool IsMultiLotString => LotsInString > 1;
}

public class LotGroupOrderDto
{
    public string ColumnName { get; set; } = "";
    public int GroupOrder { get; set; }
}

public class LotSortOrderDto
{
    public string ColumnName { get; set; } = "";
    public string Value { get; set; } = "";
    public int SortOrder { get; set; }
}

public class LotSizeRuleDto
{
    public int RuleID { get; set; }
    public string Gender { get; set; } = "";
    public string Size { get; set; } = "";
    public int MaxBoxes { get; set; }
    public int MaxSkinsPerBox { get; set; }
    public int ShowlotSkins { get; set; }
    public int MaxLotSizeExclShowlot { get; set; }
    public int MaxLotSizeInclShowlot { get; set; }
    public int Priority { get; set; }
    public bool IsActive { get; set; }
}

public class StringDefinitionDto
{
    public int StringDefinitionID { get; set; }
    public string ColumnName { get; set; } = "";
    public bool IsActive { get; set; }
}

public class CatalogNumberRuleDto
{
    public int CatalogNumberRuleID { get; set; }
    public string SalesType { get; set; } = "";
    public string Gender { get; set; } = "";
    public string Group { get; set; } = "";
    public int StartNumber { get; set; }
    public bool IsActive { get; set; }
}
