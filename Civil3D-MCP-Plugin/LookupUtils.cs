using System.Collections;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

public static class LookupUtils
{
  /// <summary>
  /// The named layer if the drawing has one, and otherwise the layer that is
  /// current. For callers where a layer is presentation: an object on the current
  /// layer is still the object that was asked for.
  /// </summary>
  public static ObjectId GetLayerId(Database database, Transaction transaction, string? layerName)
  {
    if (string.IsNullOrWhiteSpace(layerName))
    {
      return database.Clayer;
    }

    var layerTable = CivilObjectUtils.GetRequiredObject<LayerTable>(transaction, database.LayerTableId, OpenMode.ForRead);
    if (layerTable.Has(layerName))
    {
      return layerTable[layerName];
    }

    return database.Clayer;
  }

  /// <summary>
  /// The named layer, created if the drawing does not have it yet, and never
  /// something else instead.
  /// </summary>
  /// <remarks>
  /// For callers where the layer carries meaning rather than appearance: which
  /// team owns an object, and therefore whether a tool may edit it. Falling back
  /// to the current layer there is worse than failing, because the caller is told
  /// it placed the object where it asked and a later reader believes the layer it
  /// finds. That is why this exists beside <see cref="GetLayerId"/> rather than
  /// replacing it: the two answers are both right, for different questions.
  ///
  /// Creating a missing layer rather than refusing, because a scene naming a
  /// layer convention is describing the drawing it wants, and a fresh drawing
  /// legitimately has none of those layers yet.
  /// </remarks>
  public static ObjectId EnsureLayerId(Database database, Transaction transaction, string layerName)
  {
    if (string.IsNullOrWhiteSpace(layerName))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "A layer was asked for by name but the name was empty.");
    }

    var layerTable = CivilObjectUtils.GetRequiredObject<LayerTable>(transaction, database.LayerTableId, OpenMode.ForRead);
    if (layerTable.Has(layerName))
    {
      return layerTable[layerName];
    }

    // Not disposed here: the transaction takes ownership on the line below, which
    // is the same shape CivilObjectUtils.TrySetLayer already uses to add one.
    layerTable.UpgradeOpen();
    var layer = new LayerTableRecord { Name = layerName };
    var layerId = layerTable.Add(layer);
    transaction.AddNewlyCreatedDBObject(layer, true);
    return layerId;
  }

  public static ObjectId GetSiteId(CivilDocument civilDoc, Transaction transaction, string? siteName)
  {
    if (string.IsNullOrWhiteSpace(siteName))
    {
      return ObjectId.Null;
    }

    foreach (ObjectId objectId in civilDoc.GetSiteIds())
    {
      var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, objectId, OpenMode.ForRead);
      if (string.Equals(site.Name, siteName, StringComparison.OrdinalIgnoreCase))
      {
        return objectId;
      }
    }

    return ObjectId.Null;
  }

  public static ObjectId GetAlignmentStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.AlignmentStyles, transaction, styleName);
  }

  public static ObjectId GetProfileStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.ProfileStyles, transaction, styleName);
  }

  public static ObjectId GetSurfaceStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.SurfaceStyles, transaction, styleName);
  }

  public static ObjectId GetAlignmentLabelSetId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles, transaction, styleName);
  }

  public static ObjectId GetProfileLabelSetId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.LabelSetStyles.ProfileLabelSetStyles, transaction, styleName);
  }

  public static ObjectId GetProfileViewStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    var styles = CivilObjectUtils.GetPropertyValue<object>(civilDoc.Styles, "ProfileViewStyles");
    return styles != null
      ? GetStyleId(styles, transaction, styleName)
      : ObjectId.Null;
  }

  public static ObjectId GetProfileViewBandSetId(CivilDocument civilDoc, Transaction transaction, string? bandSetName)
  {
    if (string.IsNullOrWhiteSpace(bandSetName))
    {
      return ObjectId.Null;
    }

    var labelSetStyles = civilDoc.Styles.LabelSetStyles;
    var bandSetStyles = CivilObjectUtils.GetPropertyValue<object>(labelSetStyles, "ProfileViewBandSetStyles");
    return bandSetStyles != null
      ? GetStyleId(bandSetStyles, transaction, bandSetName)
      : ObjectId.Null;
  }

  public static ObjectId GetParcelStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.ParcelStyles, transaction, styleName);
  }

  public static ObjectId GetParcelAreaLabelStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.LabelStyles.ParcelLabelStyles.AreaLabelStyles, transaction, styleName);
  }

  public static ObjectId GetSectionViewStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.SectionViewStyles, transaction, styleName);
  }

  public static ObjectId GetSectionViewBandSetId(CivilDocument civilDoc, Transaction transaction, string? bandSetName)
  {
    return string.IsNullOrWhiteSpace(bandSetName)
      ? ObjectId.Null
      : GetStyleId(civilDoc.Styles.SectionViewBandSetStyles, transaction, bandSetName);
  }

  public static ObjectId GetGroupPlotStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return string.IsNullOrWhiteSpace(styleName)
      ? ObjectId.Null
      : GetStyleId(civilDoc.Styles.GroupPlotStyles, transaction, styleName);
  }

  public static string? GetFirstStyleName(object? collection, Transaction transaction)
  {
    foreach (var objectId in EnumerateObjectIds(collection))
    {
      if (objectId == ObjectId.Null)
      {
        continue;
      }

      var style = transaction.GetObject(objectId, OpenMode.ForRead);
      return CivilObjectUtils.GetName(style);
    }

    return null;
  }

  private static ObjectId GetStyleId(object collection, Transaction transaction, string? styleName)
  {
    var fallback = ObjectId.Null;

    foreach (var objectId in EnumerateObjectIds(collection))
    {
      if (objectId == ObjectId.Null)
      {
        continue;
      }

      if (fallback == ObjectId.Null)
      {
        fallback = objectId;
      }

      if (string.IsNullOrWhiteSpace(styleName))
      {
        continue;
      }

      var style = transaction.GetObject(objectId, OpenMode.ForRead);
      if (string.Equals(CivilObjectUtils.GetName(style), styleName, StringComparison.OrdinalIgnoreCase))
      {
        return objectId;
      }
    }

    return fallback;
  }

  private static IEnumerable<ObjectId> EnumerateObjectIds(object? collection)
  {
    if (collection is ObjectIdCollection objectIds)
    {
      foreach (ObjectId objectId in objectIds)
      {
        yield return objectId;
      }

      yield break;
    }

    if (collection is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item is ObjectId objectId)
        {
          yield return objectId;
        }
      }
    }
  }
}
