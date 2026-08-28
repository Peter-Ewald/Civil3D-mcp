using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

public static class PipeNetworkCommands
{
  public static Task<object?> ListPipeNetworksAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // One network throwing on some unset/quirky property (this has now
      // happened for two different properties in a row - ReferenceSurfaceId,
      // then StyleName) must not take the whole list down for every other
      // network too. Guard each individually and report which one failed
      // rather than losing the entire response.
      var networks = EnumeratePipeNetworks(civilDoc, transaction, OpenMode.ForRead)
        .Select(network =>
        {
          try
          {
            return ToPipeNetworkSummary(network, transaction);
          }
          catch (Exception ex)
          {
            return new Dictionary<string, object?>
            {
              ["name"] = TryGetName(network),
              ["handle"] = network is AcDbObject dbObject ? CivilObjectUtils.GetHandle(dbObject) : null,
              ["error"] = $"{ex.GetType().Name}: {ex.Message}",
            };
          }
        })
        .ToList();

      return new Dictionary<string, object?>
      {
        ["networks"] = networks,
      };
    });
  }

  private static string? TryGetName(Network network)
  {
    try
    {
      return network.Name;
    }
    catch
    {
      return null;
    }
  }

  public static Task<object?> GetPipeNetworkAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, name, OpenMode.ForRead);
      try
      {
        return ToPipeNetworkDetail(network, transaction);
      }
      catch (Exception ex)
      {
        // Same reasoning as ListPipeNetworksAsync's per-network guard: report
        // exactly which property failed instead of a bare unhandled-failure
        // dispatch error with no detail to act on.
        return new Dictionary<string, object?>
        {
          ["name"] = TryGetName(network),
          ["handle"] = CivilObjectUtils.GetHandle(network),
          ["error"] = $"{ex.GetType().Name}: {ex.Message}",
        };
      }
    });
  }

  public static Task<object?> GetPipeAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var pipeName = PluginRuntime.GetRequiredString(parameters, "pipeName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var pipe = FindPipeByName(network, transaction, pipeName, OpenMode.ForRead);
      return ToPipeData(pipe, transaction);
    });
  }

  public static Task<object?> GetStructureAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var structureName = PluginRuntime.GetRequiredString(parameters, "structureName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var structure = FindStructureByName(network, transaction, structureName, OpenMode.ForRead);
      return ToStructureData(structure, transaction);
    });
  }

  /// <summary>
  /// Refuses, because this command does not check anything yet.
  ///
  /// It used to resolve the two objects by name and then answer
  /// <c>interferences: []</c> and <c>totalConflicts: 0</c> whatever the drawing
  /// held. Nothing about that answer says it was never computed, so a caller
  /// asking whether a design clashes was told it does not, and a clash is exactly
  /// what such a caller stops looking for once it hears no. Refusing is the
  /// smaller failure: a caller that cannot get an answer can say so, and a person
  /// can run Autodesk's own Interference Check instead.
  ///
  /// Doing it properly means <c>Network.CreateInterferenceCheck</c> with an
  /// <c>InterferenceCriteria</c> carrying the 3D proximity distance, and reading
  /// the resulting interferences back as the pairs of parts they name. That is
  /// real work and it is not done.
  /// </summary>
  public static Task<object?> CheckPipeNetworkInterferenceAsync(JsonObject? parameters)
  {
    _ = parameters;
    throw new JsonRpcDispatchException(
      "CIVIL3D.NOT_IMPLEMENTED",
      "checkPipeNetworkInterference does not run an interference check. It is declared so that the gap is "
      + "visible, and it refuses rather than answering zero conflicts, which is what it used to do regardless "
      + "of what the drawing held. Ask a person to run Interference Check from Civil 3D's Analyze tab.");
  }

  public static Task<object?> CreatePipeNetworkAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var partsListName = PluginRuntime.GetRequiredString(parameters, "partsList");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var networkId = CreatePipeNetwork(civilDoc, name);
      var network = CivilObjectUtils.GetRequiredObject<Network>(transaction, networkId, OpenMode.ForWrite);

      var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
      if (!string.IsNullOrWhiteSpace(layerName) && network is Autodesk.AutoCAD.DatabaseServices.Entity entity)
      {
        entity.Layer = layerName;
      }

      var partsListId = FindPartsListId(civilDoc, transaction, partsListName);
      network.PartsListId = partsListId;

      var styleName = PluginRuntime.GetOptionalString(parameters, "style");
      if (!string.IsNullOrWhiteSpace(styleName))
      {
        var styleId = FindStyleId(civilDoc, transaction, styleName!, "PipeNetworkStyles", "NetworkStyles");
        if (styleId != ObjectId.Null)
        {
          network.StyleId = styleId;
        }
      }

      var referenceSurface = PluginRuntime.GetOptionalString(parameters, "referenceSurface");
      if (!string.IsNullOrWhiteSpace(referenceSurface))
      {
        var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, referenceSurface!, OpenMode.ForRead);
        network.ReferenceSurfaceId = surface.ObjectId;
      }

      var referenceAlignment = PluginRuntime.GetOptionalString(parameters, "referenceAlignment");
      if (!string.IsNullOrWhiteSpace(referenceAlignment))
      {
        var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, referenceAlignment!);
        network.ReferenceAlignmentId = alignment.ObjectId;
      }

      return new Dictionary<string, object?>
      {
        ["name"] = CivilObjectUtils.GetName(network) ?? name,
        ["handle"] = CivilObjectUtils.GetHandle(network),
        ["partsList"] = ResolveObjectName(transaction, partsListId),
        ["created"] = true,
      };
    });
  }

  // NOTE: confirmed live to have no visible effect - Structure/Pipe parts
  // render through an assigned Style whose display components hardcode
  // their own color (e.g. "Blue" for the 3D Solid and Structure Hatch
  // components), which silently overrides this. Left in place since it's
  // harmless and the parameter is still accepted, but demo coloring for
  // pipe network parts is done via separate plain-entity markers instead
  // (a small closed create3dPolyline at each structure, a duplicate
  // create3dPolyline along each pipe - see AcadCommands.cs's colorIndex).

  public static Task<object?> AddStructureToNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var partName = PluginRuntime.GetRequiredString(parameters, "partName");
    var rimElevation = PluginRuntime.GetOptionalDouble(parameters, "rimElevation") ?? 0.0;
    var sumpDepth = PluginRuntime.GetOptionalDouble(parameters, "sumpDepth") ?? 0.0;
    var structureName = PluginRuntime.GetOptionalString(parameters, "structureName");
    var colorIndex = PluginRuntime.GetOptionalInt(parameters, "colorIndex");
    var rotationDegrees = PluginRuntime.GetOptionalDouble(parameters, "rotationDegrees");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var location = new Point3d(x, y, rimElevation);
      var createdStructureId = AddStructureToNetwork(network, transaction, location, partName, rimElevation, sumpDepth, structureName);
      var structure = CivilObjectUtils.GetRequiredObject<Structure>(transaction, createdStructureId, OpenMode.ForWrite);
      CivilObjectUtils.ApplyColorIndex(structure, colorIndex);
      var rotationApplied = CivilObjectUtils.ApplyRotationDegrees(structure, rotationDegrees);
      ApplyLayer(structure, layerName, database, transaction);

      return new Dictionary<string, object?>
      {
        ["networkName"] = CivilObjectUtils.GetName(network) ?? networkName,
        ["structure"] = ToStructureData(structure, transaction),
        ["added"] = true,
        ["rotationApplied"] = rotationApplied,
        // Read back off the object rather than echoed from the request, so a
        // caller can tell whether the layer it asked for is the one it got.
        ["layer"] = structure.Layer,
      };
    });
  }

  public static Task<object?> AddPipeToNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var partName = PluginRuntime.GetRequiredString(parameters, "partName");
    var diameter = PluginRuntime.GetOptionalDouble(parameters, "diameter");
    var colorIndex = PluginRuntime.GetOptionalInt(parameters, "colorIndex");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var startStructureName = PluginRuntime.GetOptionalString(parameters, "startStructure");
      var endStructureName = PluginRuntime.GetOptionalString(parameters, "endStructure");
      var startStructureId = !string.IsNullOrWhiteSpace(startStructureName)
        ? FindStructureByName(network, transaction, startStructureName!, OpenMode.ForRead).ObjectId
        : ObjectId.Null;
      var endStructureId = !string.IsNullOrWhiteSpace(endStructureName)
        ? FindStructureByName(network, transaction, endStructureName!, OpenMode.ForRead).ObjectId
        : ObjectId.Null;

      RefuseRetiredParameter(parameters, "startPoint", "startInvert");
      RefuseRetiredParameter(parameters, "endPoint", "endInvert");

      var startInvert = ReadPoint(parameters, "startInvert");
      var endInvert = ReadPoint(parameters, "endInvert");
      var createdPipeId = AddPipeToNetwork(network, transaction, partName, diameter, startInvert, endInvert, startStructureId, endStructureId);
      var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, createdPipeId, OpenMode.ForWrite);
      // Last, and the order is the point. The part's real bore is only known once
      // the pipe exists, and connecting to a structure can move an end, so
      // converting any earlier would be undone without a word.
      ApplyInvertElevations(pipe, startInvert, endInvert);
      CivilObjectUtils.ApplyColorIndex(pipe, colorIndex);
      ApplyLayer(pipe, layerName, database, transaction);

      return new Dictionary<string, object?>
      {
        ["networkName"] = CivilObjectUtils.GetName(network) ?? networkName,
        ["pipe"] = ToPipeData(pipe, transaction),
        ["added"] = true,
        // See AddStructureToNetworkAsync: read back, not echoed.
        ["layer"] = pipe.Layer,
      };
    });
  }

  /// <summary>
  /// Lowers the pipe's ends from the centreline elevations Civil 3D stores to sit
  /// on the inverts a caller asked for.
  ///
  /// A gravity design is solved in inverts. The bottom of the bore is what has to
  /// clear a duct, what has to keep cover under a road, and what has to arrive
  /// above the pipe it discharges into. Civil 3D's <c>Pipe.StartPoint</c> and
  /// <c>EndPoint</c> are the centre of the bore instead, half a diameter higher,
  /// and nothing in the API converts between them: a gravity pipe exposes no
  /// invert elevation at all, under any of the property names this library probed
  /// for.
  ///
  /// So writing an invert straight into an endpoint buries the pipe half a
  /// diameter too deep and reports success. On this drawing's 75 mm part that is
  /// 37.5 mm, small enough that every number still looks plausible and no check
  /// anywhere catches it. This is the one place the two datums meet, on the way
  /// in, and <see cref="ToPipeData"/> is the one place they meet on the way out.
  /// </summary>
  private static void ApplyInvertElevations(Pipe pipe, Point3d? startInvert, Point3d? endInvert)
  {
    var halfBore = pipe.InnerDiameterOrWidth / 2.0;

    // The plan position is read back off the pipe rather than taken from the
    // caller's point, so a structure this end is connected to keeps deciding
    // where the end is. Only the elevation is being set here.
    if (startInvert.HasValue)
    {
      var start = pipe.StartPoint;
      pipe.StartPoint = new Point3d(start.X, start.Y, startInvert.Value.Z + halfBore);
    }

    if (endInvert.HasValue)
    {
      var end = pipe.EndPoint;
      pipe.EndPoint = new Point3d(end.X, end.Y, endInvert.Value.Z + halfBore);
    }
  }

  /// <summary>
  /// Refuses a parameter whose meaning has changed, naming what replaced it.
  ///
  /// Refused rather than reinterpreted, because the change is a datum and not a
  /// spelling: the retired names carried a centreline elevation and their
  /// replacements carry an invert. A caller still sending the old name means it
  /// still holds the old assumption, and quietly accepting it would produce a
  /// pipe half a diameter out of place with nothing failing. This is the same
  /// choice the Layer 1 contract made when it put an <c>_m</c> suffix on its
  /// diameter fields.
  /// </summary>
  private static void RefuseRetiredParameter(JsonObject? parameters, string retired, string replacement)
  {
    if (parameters?[retired] == null)
    {
      return;
    }

    throw new JsonRpcDispatchException(
      "CIVIL3D.INVALID_INPUT",
      $"'{retired}' is no longer accepted: its z was a pipe centreline elevation. Use '{replacement}', whose z is the invert, "
        + "which is the elevation a gravity design is solved in. The two differ by half the pipe's diameter.");
  }

  /// <summary>
  /// Puts a network part on a named layer, creating the layer if the drawing does
  /// not have it yet. Nothing is asked for, nothing is changed.
  /// </summary>
  /// <remarks>
  /// A pipe network part takes its layer from the network's part settings when it
  /// is created, so this is an override applied afterwards. It matters because a
  /// layer is how a caller tells the objects it owns from the ones it is only
  /// watching, and that distinction has to survive closing the drawing.
  /// </remarks>
  private static void ApplyLayer(
    Autodesk.AutoCAD.DatabaseServices.Entity part,
    string? layerName,
    Database database,
    Transaction transaction)
  {
    if (string.IsNullOrWhiteSpace(layerName))
    {
      return;
    }

    part.LayerId = LookupUtils.EnsureLayerId(database, transaction, layerName!);
  }

  /// <summary>
  /// Moves an existing structure vertically, leaving where it stands in plan
  /// alone.
  ///
  /// This and <see cref="SetPipeEndElevationsAsync"/> are the only way anything
  /// here can change a network part that already exists: every other write in
  /// this file adds one. A design that has to follow a road downwards needs
  /// exactly this, and without it a recomputed profile can be produced and never
  /// applied.
  ///
  /// In place rather than erase and recreate. The object handle, any labels
  /// attached to it and the pipe connections Civil 3D holds all survive, which is
  /// what lets a network that has been recomputed still be read back as its own
  /// record. Recreating it would hand back a different object every cycle.
  ///
  /// Both elevations are optional and at least one is required, so a caller that
  /// only needs to move the sump does not have to restate a rim it read a moment
  /// ago and risk writing a stale one back.
  /// </summary>
  public static Task<object?> SetStructureElevationsAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var structureName = PluginRuntime.GetRequiredString(parameters, "structureName");
    var rimElevation = PluginRuntime.GetOptionalDouble(parameters, "rimElevation");
    var sumpElevation = PluginRuntime.GetOptionalDouble(parameters, "sumpElevation");

    if (!rimElevation.HasValue && !sumpElevation.HasValue)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "Either 'rimElevation' or 'sumpElevation' is required: this was asked to move a structure and given nowhere to move it to.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var structure = FindStructureByName(network, transaction, structureName, OpenMode.ForWrite);

      if (rimElevation.HasValue)
      {
        structure.RimElevation = rimElevation.Value;
      }

      if (sumpElevation.HasValue)
      {
        structure.SumpElevation = sumpElevation.Value;
      }

      // Read back off the object rather than echoed from the request, so an
      // assignment Civil 3D declines to keep shows up as a number that did not
      // change rather than as the number that was asked for.
      //
      // This read is inside the same transaction as the assignment, so a property
      // Civil 3D derives on commit is not final here. A live write of a sump to
      // 98.219 answered with the 98.825 it replaced, and the drawing held 98.219
      // the moment the command returned. A caller deciding whether a write took
      // has to read the part back afterwards, through a call of its own.
      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["structure"] = ToStructureData(structure, transaction),
      };
    });
  }

  /// <summary>
  /// Moves an existing pipe's ends vertically, leaving its plan geometry alone.
  /// Civil 3D recomputes the length and the slope from the result.
  ///
  /// In place for the reasons given on <see cref="SetStructureElevationsAsync"/>.
  ///
  /// Elevations are inverts, matching addPipeToNetwork and the network read, so
  /// nothing on the path from a solved profile to the drawing converts a datum.
  /// See <see cref="ApplyInvertElevations"/> for why that matters.
  /// </summary>
  public static Task<object?> SetPipeEndElevationsAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var pipeName = PluginRuntime.GetRequiredString(parameters, "pipeName");
    var startInvert = PluginRuntime.GetOptionalDouble(parameters, "startInvert");
    var endInvert = PluginRuntime.GetOptionalDouble(parameters, "endInvert");

    if (!startInvert.HasValue && !endInvert.HasValue)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "Either 'startInvert' or 'endInvert' is required: this was asked to move a pipe end and given nowhere to move it to.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var pipe = FindPipeByName(network, transaction, pipeName, OpenMode.ForWrite);
      var halfBore = pipe.InnerDiameterOrWidth / 2.0;

      // Assigned as whole points. Point3d is a value type, so setting the Z of
      // the one a getter returned would change a copy and write nothing at all,
      // with no error to say so.
      if (startInvert.HasValue)
      {
        var start = pipe.StartPoint;
        pipe.StartPoint = new Point3d(start.X, start.Y, startInvert.Value + halfBore);
      }

      if (endInvert.HasValue)
      {
        var end = pipe.EndPoint;
        pipe.EndPoint = new Point3d(end.X, end.Y, endInvert.Value + halfBore);
      }

      // The whole pipe read back, which is also how a caller sees whether the
      // structures at either end are still connected: `startStructure` and
      // `endStructure` come back null if moving an end detached it.
      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["pipe"] = ToPipeData(pipe, transaction),
      };
    });
  }

  public static Task<object?> ResizePipeInNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var pipeName = PluginRuntime.GetRequiredString(parameters, "pipeName");
    var newPartName = PluginRuntime.GetOptionalString(parameters, "newPartName");
    var newDiameter = PluginRuntime.GetOptionalDouble(parameters, "newDiameter");

    if (string.IsNullOrWhiteSpace(newPartName) && !newDiameter.HasValue)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Either 'newPartName' or 'newDiameter' is required.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var pipe = FindPipeByName(network, transaction, pipeName, OpenMode.ForWrite);

      if (!string.IsNullOrWhiteSpace(newPartName))
      {
        var part = FindPartForNetwork(network, transaction, newPartName!, DomainType.Pipe);
        pipe.SwapPartFamilyAndSize(part.FamilyId, part.SizeId);
      }

      if (newDiameter.HasValue)
      {
        pipe.ResizeByInnerDiameterOrWidth(newDiameter.Value, useClosestSize: false);
      }

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["pipeName"] = pipeName,
        ["newPartName"] = newPartName,
        ["newDiameter"] = newDiameter,
        ["resized"] = true,
      };
    });
  }

  public static Task<object?> ListPipePartsCatalogAsync(JsonObject? parameters)
  {
    var partsListName = PluginRuntime.GetOptionalString(parameters, "partsList");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var partsLists = EnumeratePartsLists(civilDoc, transaction)
        .Where(partsList => string.IsNullOrWhiteSpace(partsListName) || string.Equals(GetPartsListName(partsList), partsListName, StringComparison.OrdinalIgnoreCase))
        .Select(partsList => new Dictionary<string, object?>
        {
          ["name"] = GetPartsListName(partsList),
          ["handle"] = partsList is AcDbObject dbObject ? CivilObjectUtils.GetHandle(dbObject) : null,
          ["parts"] = EnumeratePartNames(partsList, transaction).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToList(),
          // Reported separately as well as flat, because a caller choosing a
          // part has to know which domain it belongs to: passing a pipe family
          // where a structure belongs is accepted here and refused later by
          // Civil 3D itself. Additive, so a caller reading only "parts" is
          // unaffected.
          ["pipeParts"] = EnumerateSelectablePartNames(partsList, transaction, DomainType.Pipe),
          ["structureParts"] = EnumerateSelectablePartNames(partsList, transaction, DomainType.Structure),
        })
        .ToList();

      return new Dictionary<string, object?>
      {
        ["partsLists"] = partsLists,
      };
    });
  }

  public static int CountPipeNetworks(object civilDoc)
  {
    return ((CivilDocument)civilDoc).GetPipeNetworkIds().Count;
  }

  public static string? GetFirstPipeNetworkStyleName(object civilDoc, Transaction transaction)
  {
    var styles = ((CivilDocument)civilDoc).Styles;
    var collection = Civil3DCompatibility.GetPropertyValue(styles, "PipeNetworkStyles")
      ?? Civil3DCompatibility.GetPropertyValue(styles, "NetworkStyles");
    return LookupUtils.GetFirstStyleName(collection, transaction);
  }

  private static IEnumerable<Network> EnumeratePipeNetworks(object civilDoc, Transaction transaction, OpenMode openMode)
  {
    foreach (ObjectId objectId in ((CivilDocument)civilDoc).GetPipeNetworkIds())
    {
      yield return CivilObjectUtils.GetRequiredObject<Network>(transaction, objectId, openMode);
    }
  }

  private static IEnumerable<ObjectId> GetPipeNetworkIds(object civilDoc)
  {
    var candidates = new[]
    {
      CivilObjectUtils.InvokeMethod(civilDoc, "GetPipeNetworkIds"),
      GetNamedMemberValue(civilDoc, "PipeNetworkCollection"),
      GetNamedMemberValue(civilDoc, "NetworkCollection"),
      GetNamedMemberValue(civilDoc, "PipeNetworks"),
      GetNamedMemberValue(civilDoc, "Networks"),
    };

    foreach (var candidate in candidates)
    {
      foreach (var objectId in ToObjectIdsFlexible(candidate))
      {
        if (objectId != ObjectId.Null)
        {
          yield return objectId;
        }
      }
    }
  }

  // CivilObjectUtils.ToObjectIds already walks a plain IEnumerable itself (its
  // own fallback branch for anything that isn't an ObjectIdCollection) - so
  // this can't call that helper *and then also* walk `value` as IEnumerable
  // again below, or every item comes out twice. This method's only reason to
  // exist alongside that helper is the extra fallback for items that aren't a
  // raw ObjectId but expose one via an .ObjectId/.Id property (GetAnyObjectId)
  // - so inline the ObjectIdCollection fast path directly instead of
  // delegating to it, and do the generic-enumerable walk exactly once.
  private static IEnumerable<ObjectId> ToObjectIdsFlexible(object? value)
  {
    if (value is ObjectIdCollection objectIds)
    {
      foreach (ObjectId objectId in objectIds)
      {
        yield return objectId;
      }
      yield break;
    }

    if (value is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item is ObjectId objectId && objectId != ObjectId.Null)
        {
          yield return objectId;
          continue;
        }

        var itemObjectId = GetAnyObjectId(item, "ObjectId", "Id");
        if (itemObjectId != ObjectId.Null)
        {
          yield return itemObjectId;
        }
      }
    }
  }

  private static Network FindPipeNetworkByName(object civilDoc, Transaction transaction, string name, OpenMode openMode)
  {
    foreach (var network in EnumeratePipeNetworks(civilDoc, transaction, openMode))
    {
      if (string.Equals(CivilObjectUtils.GetName(network), name, StringComparison.OrdinalIgnoreCase))
      {
        return network;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe network '{name}' was not found.");
  }

  private static Pipe FindPipeByName(Network network, Transaction transaction, string pipeName, OpenMode openMode)
  {
    foreach (ObjectId objectId in network.GetPipeIds())
    {
      var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, objectId, openMode);
      if (string.Equals(pipe.Name, pipeName, StringComparison.OrdinalIgnoreCase))
      {
        return pipe;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe '{pipeName}' was not found in network '{CivilObjectUtils.GetName(network)}'.");
  }

  private static Structure FindStructureByName(Network network, Transaction transaction, string structureName, OpenMode openMode)
  {
    foreach (ObjectId objectId in network.GetStructureIds())
    {
      var structure = CivilObjectUtils.GetRequiredObject<Structure>(transaction, objectId, openMode);
      if (string.Equals(structure.Name, structureName, StringComparison.OrdinalIgnoreCase))
      {
        return structure;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Structure '{structureName}' was not found in network '{CivilObjectUtils.GetName(network)}'.");
  }

  private static Dictionary<string, object?> ToPipeNetworkSummary(Network network, Transaction transaction)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = network.Name,
      ["handle"] = CivilObjectUtils.GetHandle(network),
      ["pipeCount"] = network.GetPipeIds().Count,
      ["structureCount"] = network.GetStructureIds().Count,
      ["surface"] = ResolveObjectName(transaction, GetReferenceId(() => network.ReferenceSurfaceId)),
    };
  }

  private static Dictionary<string, object?> ToPipeNetworkDetail(Network network, Transaction transaction)
  {
    var pipes = network.GetPipeIds().Cast<ObjectId>()
      .Select(objectId => ToPipeData(CivilObjectUtils.GetRequiredObject<Pipe>(transaction, objectId, OpenMode.ForRead), transaction))
      .ToList();
    var structures = network.GetStructureIds().Cast<ObjectId>()
      .Select(objectId => ToStructureData(CivilObjectUtils.GetRequiredObject<Structure>(transaction, objectId, OpenMode.ForRead), transaction))
      .ToList();

    return new Dictionary<string, object?>
    {
      ["name"] = network.Name,
      ["handle"] = CivilObjectUtils.GetHandle(network),
      ["partsList"] = ResolveObjectName(transaction, network.PartsListId),
      ["style"] = TryGet(() => network.StyleName),
      ["referenceSurface"] = ResolveObjectName(transaction, GetReferenceId(() => network.ReferenceSurfaceId)),
      ["referenceAlignment"] = ResolveObjectName(transaction, GetReferenceId(() => network.ReferenceAlignmentId)),
      ["pipes"] = pipes,
      ["structures"] = structures,
    };
  }

  // Several optional/unset Network properties (confirmed so far:
  // ReferenceSurfaceId, ReferenceAlignmentId, StyleName - found one at a time,
  // each requiring its own rebuild-and-retest cycle) throw a native
  // CivilException ("Retrieve attribute failed") instead of returning
  // ObjectId.Null/null when nothing is assigned - this project's own test
  // network never sets a style or reference surface/alignment. Route every
  // such optional read through one of these two helpers instead of adding
  // another one-off try/catch the next time a new property turns out to have
  // the same quirk.
  private static ObjectId GetReferenceId(Func<ObjectId> getter)
  {
    try
    {
      return getter();
    }
    catch
    {
      return ObjectId.Null;
    }
  }

  private static T? TryGet<T>(Func<T?> getter)
  {
    try
    {
      return getter();
    }
    catch
    {
      return default;
    }
  }

  private static Dictionary<string, object?> ToPipeData(Pipe pipe, Transaction transaction)
  {
    // Derived, because Civil 3D does not expose it. A gravity pipe was probed on a
    // live 2026 drawing for StartInvertElevation, InvertIn and StartInnerElevation
    // and answered none of them, so the endpoints are all there is and they are
    // centrelines. Converting here rather than leaving it to every caller is what
    // keeps this library answering in the datum a gravity design is solved in,
    // and it is the outbound half of the pair with ApplyInvertElevations.
    var halfBore = pipe.InnerDiameterOrWidth / 2.0;

    return new Dictionary<string, object?>
    {
      ["name"] = pipe.Name,
      ["handle"] = CivilObjectUtils.GetHandle(pipe),
      // Which layer this pipe is on, so a caller can tell what it drew itself
      // from what it was given. Answering that from the drawing rather than from
      // a record of the conversation is what lets a later session, or nobody at
      // all, work out which parts it may change.
      ["layer"] = pipe.Layer,
      ["startStructure"] = ResolveObjectName(transaction, pipe.StartStructureId),
      ["endStructure"] = ResolveObjectName(transaction, pipe.EndStructureId),
      ["length"] = pipe.Length3D,
      ["diameter"] = pipe.InnerDiameterOrWidth,
      ["slope"] = pipe.Slope,
      ["material"] = pipe.Material,
      // Where the pipe actually is, not only how deep it is. A caller checking a
      // new route against existing infrastructure needs the plan geometry, and
      // without it the only way to place an existing pipe is to infer it from the
      // structures somebody named in a sentence. It is also the only way to tell
      // apart pipes that Civil 3D named from its own counter: the endpoints match
      // the structures they run between.
      ["startPoint"] = CivilObjectUtils.ToPointData(pipe.StartPoint),
      ["endPoint"] = CivilObjectUtils.ToPointData(pipe.EndPoint),
      ["invertIn"] = pipe.StartPoint.Z - halfBore,
      ["invertOut"] = pipe.EndPoint.Z - halfBore,
      ["elevationNote"] = "startPoint.z and endPoint.z are centreline elevations, as Civil 3D stores them. invertIn and invertOut are the same ends as inverts, half the bore lower, which is what a gravity design is solved in.",
    };
  }

  private static Dictionary<string, object?> ToStructureData(Structure structure, Transaction transaction)
  {
    var connectedPipeIds = Enumerable.Range(0, structure.ConnectedPipesCount)
      .Select(index => Civil3DCompatibility.GetIndexedPropertyValue(structure, "ConnectedPipe", index))
      .OfType<ObjectId>();

    return new Dictionary<string, object?>
    {
      ["name"] = structure.Name,
      ["handle"] = CivilObjectUtils.GetHandle(structure),
      // As on a pipe, and for the same reason.
      ["layer"] = structure.Layer,
      ["type"] = structure.PartType.ToString(),
      ["rimElevation"] = structure.RimElevation,
      ["sumpElevation"] = structure.SumpElevation,
      ["x"] = structure.Location.X,
      ["y"] = structure.Location.Y,
      ["connectedPipes"] = connectedPipeIds.Select(objectId => ResolveObjectName(transaction, objectId) ?? objectId.Handle.ToString()).ToList(),
    };
  }

  private static ObjectId CreatePipeNetwork(object civilDoc, string name)
  {
    var requestedName = name;
    return Network.Create((CivilDocument)civilDoc, ref requestedName);
  }

  private static ObjectId AddStructureToNetwork(Network network, Transaction transaction, Point3d location, string partName, double rimElevation, double sumpDepth, string? structureName = null)
  {
    var part = FindPartForNetwork(network, transaction, partName, DomainType.Structure);
    var createdId = ObjectId.Null;
    try
    {
      network.AddStructure(part.FamilyId, part.SizeId, location, 0.0, ref createdId, applyRules: false);
    }
    catch (InvalidOperationException ex)
    {
      // Civil 3D accepts any structure-domain family here and then refuses the
      // ones it cannot place - an end section, for instance, where a junction is
      // required. Its own reason is kept, since it is the accurate one, and the
      // parts a caller could have chosen instead are added to it: reported as
      // anything other than invalid input, this reads as a plugin fault rather
      // than as a part name to change.
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Civil 3D would not place a structure from part '{partName}': {ex.Message} "
        + $"Structure parts available in this network's parts list: {DescribeSelectableStructureParts(network, transaction)}.");
    }
    var structure = CivilObjectUtils.GetRequiredObject<Structure>(transaction, createdId, OpenMode.ForWrite);
    structure.RimElevation = rimElevation;
    structure.RimToSumpHeight = sumpDepth;
    // Civil 3D's AddStructure has no name parameter - it always auto-assigns
    // from a per-drawing counter that only ever increases, so a caller has no
    // way to know the name in advance and it drifts every time this runs
    // against the same drawing. Rename right away when an explicit name was
    // requested, same as any other Civil 3D object rename.
    if (!string.IsNullOrWhiteSpace(structureName))
    {
      structure.Name = structureName;
    }
    return createdId;
  }

  private static ObjectId AddPipeToNetwork(Network network, Transaction transaction, string partName, double? diameter, Point3d? startPoint, Point3d? endPoint, ObjectId startStructureId, ObjectId endStructureId)
  {
    var start = startPoint ?? GetStructureLocation(transaction, startStructureId, "start");
    var end = endPoint ?? GetStructureLocation(transaction, endStructureId, "end");
    var part = FindPartForNetwork(network, transaction, partName, DomainType.Pipe);
    var createdId = ObjectId.Null;
    network.AddLinePipe(part.FamilyId, part.SizeId, new LineSegment3d(start, end), ref createdId, applyRules: false);
    var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, createdId, OpenMode.ForWrite);
    if (startStructureId != ObjectId.Null)
      pipe.ConnectToStructure(ConnectorPositionType.Start, startStructureId, force: true);
    if (endStructureId != ObjectId.Null)
      pipe.ConnectToStructure(ConnectorPositionType.End, endStructureId, force: true);
    if (diameter.HasValue && Math.Abs(pipe.InnerDiameterOrWidth - diameter.Value) > 1.0e-6)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Part size '{partName}' has inner diameter/width {pipe.InnerDiameterOrWidth}, not the requested {diameter.Value}. The pipe was not committed.");
    return createdId;
  }

  private static IEnumerable<ObjectId> GetChildObjectIds(object owner, params string[] memberNames)
  {
    foreach (var memberName in memberNames)
    {
      object? memberValue = CivilObjectUtils.InvokeMethod(owner, memberName) ?? GetNamedMemberValue(owner, memberName);
      foreach (var objectId in ToObjectIdsFlexible(memberValue))
      {
        if (objectId != ObjectId.Null)
        {
          yield return objectId;
        }
      }
    }
  }

  private static object? GetNamedMemberValue(object? value, string memberName)
  {
    return Civil3DCompatibility.GetPropertyValue(value, memberName)
      ?? Civil3DCompatibility.GetFieldValue(value, memberName);
  }

  private static string? ResolveObjectName(Transaction transaction, ObjectId objectId)
  {
    if (objectId == ObjectId.Null)
    {
      return null;
    }

    try
    {
      var dbObject = transaction.GetObject(objectId, OpenMode.ForRead);
      return CivilObjectUtils.GetName(dbObject);
    }
    catch
    {
      return null;
    }
  }

  private static ObjectId GetAnyObjectId(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var objectId = CivilObjectUtils.GetPropertyValue<ObjectId>(value, propertyName);
      if (objectId != ObjectId.Null)
      {
        return objectId;
      }
    }

    return ObjectId.Null;
  }

  private static double? GetAnyDouble(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var propertyValue = CivilObjectUtils.GetPropertyValue<double?>(value, propertyName);
      if (propertyValue.HasValue)
      {
        return propertyValue.Value;
      }
    }

    return null;
  }

  private static string? GetAnyString(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var propertyValue = CivilObjectUtils.GetStringProperty(value, propertyName);
      if (!string.IsNullOrWhiteSpace(propertyValue))
      {
        return propertyValue;
      }
    }

    return null;
  }

  private static Point3d? GetPointProperty(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var propertyValue = CivilObjectUtils.GetPropertyValue<Point3d?>(value, propertyName);
      if (propertyValue.HasValue)
      {
        return propertyValue.Value;
      }
    }

    return null;
  }

  private static double Distance(Point3d? startPoint, Point3d? endPoint)
  {
    if (!startPoint.HasValue || !endPoint.HasValue)
    {
      return 0.0;
    }

    return startPoint.Value.DistanceTo(endPoint.Value);
  }

  private static double CalculateSlope(Point3d? startPoint, Point3d? endPoint)
  {
    if (!startPoint.HasValue || !endPoint.HasValue)
    {
      return 0.0;
    }

    var horizontal = Math.Sqrt(Math.Pow(endPoint.Value.X - startPoint.Value.X, 2) + Math.Pow(endPoint.Value.Y - startPoint.Value.Y, 2));
    if (horizontal <= 1.0e-9)
    {
      return 0.0;
    }

    return (endPoint.Value.Z - startPoint.Value.Z) / horizontal;
  }

  private static Point3d? ReadPoint(JsonObject? parameters, string parameterName)
  {
    if (PluginRuntime.GetParameter(parameters, parameterName) is not JsonObject pointNode)
    {
      return null;
    }

    var x = pointNode["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{parameterName}.x is required.");
    var y = pointNode["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{parameterName}.y is required.");
    var z = pointNode["z"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{parameterName}.z is required; elevation 0 will not be assumed.");
    return new Point3d(x, y, z);
  }

  private static ObjectId FindStyleId(object civilDoc, Transaction transaction, string styleName, params string[] collectionNames)
  {
    var styles = GetNamedMemberValue(civilDoc, "Styles");
    if (styles == null)
    {
      return ObjectId.Null;
    }

    foreach (var collectionName in collectionNames)
    {
      var collection = GetNamedMemberValue(styles, collectionName);
      foreach (var objectId in CivilObjectUtils.ToObjectIds(collection))
      {
        if (objectId == ObjectId.Null)
        {
          continue;
        }

        var style = transaction.GetObject(objectId, OpenMode.ForRead);
        if (string.Equals(CivilObjectUtils.GetName(style), styleName, StringComparison.OrdinalIgnoreCase))
        {
          return objectId;
        }
      }
    }

    return ObjectId.Null;
  }

  private static ObjectId FindPartsListId(object civilDoc, Transaction transaction, string partsListName)
  {
    foreach (var partsList in EnumeratePartsLists(civilDoc, transaction))
    {
      if (string.Equals(GetPartsListName(partsList), partsListName, StringComparison.OrdinalIgnoreCase) && partsList is AcDbObject dbObject)
      {
        return dbObject.ObjectId;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Parts list '{partsListName}' was not found.");
  }

  // PartsList.Name is a documented, stable Autodesk API member (confirmed via
  // reflection against the real AeccDbMgd.dll), but Civil3DCompatibility's
  // generic PropertyInfo.GetValue()-based lookup throws "Property Get method
  // was not found" for it - a known limitation of reflecting over certain
  // C++/CLI-emitted properties on Autodesk's managed API. Access it directly
  // with a strong-typed cast instead, per this file's own documented
  // convention of calling documented members directly rather than through the
  // generic reflection compatibility layer (see Civil3DCompatibility.cs).
  private static string? GetPartsListName(object value)
  {
    return value is PartsList partsList ? partsList.Name : CivilObjectUtils.GetName(value);
  }

  private static IEnumerable<object> EnumeratePartsLists(object civilDoc, Transaction transaction)
  {
    var styles = GetNamedMemberValue(civilDoc, "Styles");
    if (styles == null)
    {
      yield break;
    }

    // These four names are fallback candidates for the *same* logical
    // collection across different Civil 3D API surfaces/versions - not four
    // separate collections to union. Stop at the first one that actually
    // resolves, or a version exposing more than one of these names (as this
    // one does) yields every parts list twice.
    foreach (var collectionName in new[] { "PartsListSet", "PartsLists", "PartsListCollection", "PartsListStyles" })
    {
      var collection = GetNamedMemberValue(styles, collectionName) ?? GetNamedMemberValue(civilDoc, collectionName);
      if (collection == null)
      {
        continue;
      }

      foreach (var objectId in ToObjectIdsFlexible(collection))
      {
        yield return transaction.GetObject(objectId, OpenMode.ForRead)!;
      }

      yield break;
    }
  }

  /// <summary>
  /// The part names in one domain that a caller may actually pass as
  /// <c>partName</c>. Deliberately mirrors what <see cref="FindPartForNetwork"/>
  /// matches on rather than listing whatever is easiest to enumerate: a part size's
  /// own name, plus the family name where the family holds exactly one size. A
  /// catalog that lists names resolution then rejects is worse than no catalog.
  /// </summary>
  private static List<string> EnumerateSelectablePartNames(object partsListObject, Transaction transaction, DomainType domain)
  {
    var names = new List<string>();
    if (partsListObject is not PartsList partsList)
    {
      return names;
    }

    foreach (ObjectId familyId in partsList.GetPartFamilyIdsByDomain(domain))
    {
      if (transaction.GetObject(familyId, OpenMode.ForRead) is not PartFamily family)
      {
        continue;
      }

      if (family.PartSizeCount == 1 && !string.IsNullOrWhiteSpace(family.Name))
      {
        names.Add(family.Name);
      }

      for (var index = 0; index < family.PartSizeCount; index++)
      {
        var size = transaction.GetObject(family[index], OpenMode.ForRead);
        var sizeName = CivilObjectUtils.GetName(size) ?? CivilObjectUtils.GetStringProperty(size, "Description");
        if (!string.IsNullOrWhiteSpace(sizeName))
        {
          names.Add(sizeName!);
        }
      }
    }

    return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
  }

  private static IEnumerable<string> EnumeratePartNames(object partsListObject, Transaction transaction)
  {
    // Same reflection limitation as GetPartsListName above: PartFamilyCount,
    // the int indexer, and PartFamily.Name are all documented, stable members -
    // access them directly with strong-typed casts rather than through
    // Civil3DCompatibility's generic reflection helpers.
    if (partsListObject is PartsList partsList)
    {
      for (var i = 0; i < partsList.PartFamilyCount; i++)
      {
        var familyId = partsList[i];
        if (familyId.IsNull)
        {
          continue;
        }

        if (transaction.GetObject(familyId, OpenMode.ForRead) is PartFamily family
          && !string.IsNullOrWhiteSpace(family.Name))
        {
          yield return family.Name;
        }
      }

      yield break;
    }

    // Fallback for any other object shape that does expose a named collection
    // (e.g. a different Civil 3D host version than the one this was verified against).
    foreach (var collectionName in new[] { "PartFamilies", "PipeFamilies", "StructureFamilies", "PartFamilySet" })
    {
      var collection = GetNamedMemberValue(partsListObject, collectionName);
      foreach (var item in EnumerateNamedObjects(collection))
      {
        var familyName = CivilObjectUtils.GetName(item);
        if (!string.IsNullOrWhiteSpace(familyName))
        {
          yield return familyName!;
        }

        foreach (var child in EnumerateNamedObjects(GetNamedMemberValue(item, "PartSizeFilter") ?? GetNamedMemberValue(item, "PartSizes") ?? GetNamedMemberValue(item, "SizeDataRecords")))
        {
          var childName = CivilObjectUtils.GetName(child) ?? CivilObjectUtils.GetStringProperty(child, "Description");
          if (!string.IsNullOrWhiteSpace(childName))
          {
            yield return childName!;
          }
        }
      }
    }
  }

  private static IEnumerable<object> EnumerateNamedObjects(object? collection)
  {
    if (collection is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item == null)
        {
          continue;
        }

        yield return item;
      }
    }
  }

  private readonly record struct NetworkPartIds(ObjectId FamilyId, ObjectId SizeId);

  private static NetworkPartIds FindPartForNetwork(Network network, Transaction transaction, string partName, DomainType domain)
  {
    if (network.PartsListId.IsNull)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe network '{network.Name}' does not have a parts list assigned.");
    }

    var partsList = CivilObjectUtils.GetRequiredObject<PartsList>(transaction, network.PartsListId, OpenMode.ForRead);
    foreach (ObjectId familyId in partsList.GetPartFamilyIdsByDomain(domain))
    {
      var family = CivilObjectUtils.GetRequiredObject<PartFamily>(transaction, familyId, OpenMode.ForRead);
      for (var index = 0; index < family.PartSizeCount; index++)
      {
        var sizeId = family[index];
        var size = transaction.GetObject(sizeId, OpenMode.ForRead);
        var sizeName = CivilObjectUtils.GetName(size) ?? CivilObjectUtils.GetStringProperty(size, "Description");
        if (string.Equals(sizeName, partName, StringComparison.OrdinalIgnoreCase)
          || (family.PartSizeCount == 1 && string.Equals(family.Name, partName, StringComparison.OrdinalIgnoreCase)))
          return new NetworkPartIds(familyId, sizeId);
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Exact {domain} size '{partName}' was not found in the parts list for network '{network.Name}'.");
  }

  private static string DescribeSelectableStructureParts(Network network, Transaction transaction)
  {
    if (network.PartsListId.IsNull)
    {
      return "none, because this network has no parts list assigned";
    }

    var partsList = CivilObjectUtils.GetRequiredObject<PartsList>(transaction, network.PartsListId, OpenMode.ForRead);
    var names = EnumerateSelectablePartNames(partsList, transaction, DomainType.Structure);
    return names.Count == 0 ? "none" : string.Join(", ", names);
  }

  private static Point3d GetStructureLocation(Transaction transaction, ObjectId structureId, string endpointName)
  {
    if (structureId.IsNull)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Provide {endpointName}Point or {endpointName}Structure; an origin point will not be assumed.");
    return CivilObjectUtils.GetRequiredObject<Structure>(transaction, structureId, OpenMode.ForRead).Location;
  }
}
