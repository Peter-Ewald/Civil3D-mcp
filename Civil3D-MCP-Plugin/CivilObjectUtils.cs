using System.Collections;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

public static class CivilObjectUtils
{
  /// <summary>
  /// A coordinate as every other coordinate on this interface is reported: three
  /// named numbers in the drawing's own units, which for a metric drawing means
  /// metres. Shared, so that a caller reading a pipe's geometry and a caller
  /// reading a structure's get the same three field names.
  /// </summary>
  public static Dictionary<string, object?> ToPointData(Autodesk.AutoCAD.Geometry.Point3d point)
  {
    return new Dictionary<string, object?>
    {
      ["x"] = point.X,
      ["y"] = point.Y,
      ["z"] = point.Z,
    };
  }

  public static string GetHandle(AcDbObject dbObject)
  {
    return dbObject.Handle.ToString();
  }

  // AutoCAD Color Index (1-255; 1=red, 2=yellow, 3=green, etc.) applied
  // directly on the entity, overriding ByLayer - a demo/visualization aid
  // (distinguishing existing-vs-new, obstacle-vs-endpoint geometry in
  // recordings) with no bearing on any object's own engineering data. Only
  // effective on plain AutoCAD entities (Polyline3d, Circle, etc.) - Civil 3D
  // parts (Structure, Pipe) render through an assigned Style whose display
  // components can hardcode their own color, silently overriding this (found
  // live: this had no visible effect on pipe network structures/pipes at all).
  public static void ApplyColorIndex(Autodesk.AutoCAD.DatabaseServices.Entity entity, int? colorIndex)
  {
    if (colorIndex is int index)
    {
      entity.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)index);
    }
  }

  // Structure.Rotation (radians, CCW from the WCS +X axis) orients the
  // structure's inserted part in plan - unlike color, it isn't overridden by
  // the assigned Style, so this actually has a visible effect. Left unset,
  // new structures default to rotation 0 regardless of which way their
  // connected pipes run.
  //
  // Civil3DCompatibility.TrySetProperty swallows any exception and returns
  // false rather than throwing (a missing/unwritable "Rotation" property on
  // this Structure type would fail exactly that way) - returning that bool
  // here, rather than discarding it, so a silent failure shows up in the API
  // response instead of just looking like "nothing happened".
  public static bool ApplyRotationDegrees(AcDbObject structure, double? rotationDegrees)
  {
    if (rotationDegrees is double degrees)
    {
      return Civil3DCompatibility.TrySetProperty(structure, "Rotation", degrees * Math.PI / 180.0);
    }
    return true;
  }

  public static string? GetName(object? value)
  {
    if (value == null)
    {
      return null;
    }

    return Civil3DCompatibility.GetPropertyValue(value, "Name")?.ToString();
  }

  public static string? GetStringProperty(object? value, string propertyName)
  {
    if (value == null)
    {
      return null;
    }

    return Civil3DCompatibility.GetPropertyValue(value, propertyName)?.ToString();
  }

  public static T? GetPropertyValue<T>(object? value, string propertyName)
  {
    if (value == null)
    {
      return default;
    }

    return Civil3DCompatibility.GetPropertyValue<T>(value, propertyName);
  }

  public static object? InvokeMethod(object? value, string methodName, params object?[] arguments)
  {
    if (value == null)
    {
      return null;
    }

    return Civil3DCompatibility.InvokeMethod(value, methodName, arguments);
  }

  public static IEnumerable<ObjectId> ToObjectIds(object? collection)
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

  public static T GetRequiredObject<T>(Transaction transaction, ObjectId objectId, OpenMode openMode) where T : AcDbObject
  {
    return (T)(transaction.GetObject(objectId, openMode) ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Object {objectId} not found."));
  }

  public static string LinearUnits(Database database)
  {
    if (database.Insunits == UnitsValue.Meters) return "meters";
    if (database.Insunits == UnitsValue.Feet) return "feet";
    if (database.Insunits.ToString().Contains("Survey", StringComparison.OrdinalIgnoreCase)) return "feet";
    return "other";
  }

  public static string AngularUnits(short aunits)
  {
    return aunits switch
    {
      0 => "degrees",
      1 => "degrees",
      2 => "grads",
      3 => "radians",
      _ => "degrees",
    };
  }

  public static Alignment FindAlignmentByName(CivilDocument civilDoc, Transaction transaction, string name)
  {
    foreach (ObjectId objectId in civilDoc.GetAlignmentIds())
    {
      var alignment = GetRequiredObject<Alignment>(transaction, objectId, OpenMode.ForRead);
      if (string.Equals(alignment.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return alignment;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Alignment '{name}' was not found.");
  }

  public static Profile FindProfileByName(Alignment alignment, Transaction transaction, string name, OpenMode openMode)
  {
    foreach (ObjectId objectId in alignment.GetProfileIds())
    {
      var profile = GetRequiredObject<Profile>(transaction, objectId, openMode);
      if (string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return profile;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Profile '{name}' was not found on alignment '{alignment.Name}'.");
  }

  public static CivilSurface FindSurfaceByName(CivilDocument civilDoc, Transaction transaction, string name, OpenMode openMode)
  {
    foreach (ObjectId objectId in civilDoc.GetSurfaceIds())
    {
      var surface = GetRequiredObject<CivilSurface>(transaction, objectId, openMode);
      if (string.Equals(surface.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return surface;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Surface '{name}' was not found.");
  }

  public static Corridor FindCorridorByName(CivilDocument civilDoc, Transaction transaction, string name, OpenMode openMode)
  {
    foreach (ObjectId objectId in civilDoc.CorridorCollection)
    {
      var corridor = GetRequiredObject<Corridor>(transaction, objectId, openMode);
      if (string.Equals(corridor.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return corridor;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Corridor '{name}' was not found.");
  }

  public static Database GetDatabase(object? civilDoc)
  {
    if (civilDoc != null)
    {
      var db = GetPropertyValue<Database>(civilDoc, "Database");
      if (db != null)
      {
        return db;
      }
    }

    return HostApplicationServices.WorkingDatabase
      ?? throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "No active AutoCAD database is available.");
  }

  public static ObjectId GetModelSpaceBlockId(Database database, Transaction transaction)
  {
    var blockTable = GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
    return blockTable[BlockTableRecord.ModelSpace];
  }

  public static double? GetDoubleProperty(object? value, string propertyName)
  {
    if (value == null) return null;
    var raw = Civil3DCompatibility.GetPropertyValue(value, propertyName);
    if (raw == null) return null;
    try { return Convert.ToDouble(raw); } catch { return null; }
  }

  public static bool? GetBoolProperty(object? value, string propertyName)
  {
    if (value == null) return null;
    var raw = Civil3DCompatibility.GetPropertyValue(value, propertyName);
    if (raw == null) return null;
    try { return Convert.ToBoolean(raw); } catch { return null; }
  }

  public static string VolumeUnits(Database database)
  {
    if (database.Insunits == UnitsValue.Meters) return "cubic meters";
    if (database.Insunits == UnitsValue.Feet) return "cubic feet";
    if (database.Insunits.ToString().Contains("Survey", StringComparison.OrdinalIgnoreCase)) return "cubic feet";
    return "cubic units";
  }

  public static void TrySetName(AcDbObject obj, string name)
  {
    Civil3DCompatibility.TrySetProperty(obj, "Name", name);
  }

  public static object? InvokeStaticMethod(Type type, string methodName, params object?[] arguments)
  {
    return Civil3DCompatibility.InvokeStaticMethod(type, methodName, arguments);
  }

  public static void TrySetLayer(AcDbObject obj, string layer, Database database, Transaction transaction)
  {
    try
    {
      var layerTable = transaction.GetObject(database.LayerTableId, OpenMode.ForRead) as LayerTable;
      if (layerTable == null) return;
      if (!layerTable.Has(layer))
      {
        var lt = transaction.GetObject(database.LayerTableId, OpenMode.ForWrite) as LayerTable;
        var ltr = new LayerTableRecord { Name = layer };
        lt!.Add(ltr);
        transaction.AddNewlyCreatedDBObject(ltr, true);
      }
      Civil3DCompatibility.TrySetProperty(obj, "Layer", layer);
    }
    catch { /* ignore layer errors */ }
  }

  public static Dictionary<string, object?> ToPointData(CogoPoint point)
  {
    return new Dictionary<string, object?>
    {
      ["number"] = point.PointNumber,
      ["name"] = point.PointName,
      ["x"] = point.Location.X,
      ["y"] = point.Location.Y,
      ["z"] = point.Location.Z,
      ["rawDescription"] = point.RawDescription,
      ["fullDescription"] = point.FullDescription,
    };
  }
}
