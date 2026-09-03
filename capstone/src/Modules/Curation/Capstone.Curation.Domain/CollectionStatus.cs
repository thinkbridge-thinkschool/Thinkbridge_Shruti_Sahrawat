namespace Capstone.Curation.Domain;

public enum CollectionStatus
{
    /// <summary>Private to its curator. Freely editable. Nobody else can see it.</summary>
    Draft = 0,

    /// <summary>Visible to followers, and frozen - see Collection.Publish.</summary>
    Published = 1,
}
