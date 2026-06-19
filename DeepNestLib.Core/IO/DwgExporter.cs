namespace DeepNestLib.IO
{
  using ACadSharp;
  using ACadSharp.Entities;
  using ACadSharp.IO;
  using ACadSharp.Tables;
  using ACadSharp.Types.Units;
  using CSMath;
  using DeepNestLib.Geometry;
  using DeepNestLib.Placement;
  using System;
  using System.Collections.Generic;
  using System.Drawing; // Required for PointF
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Threading.Tasks;

  public class DwgExporter : ExporterBase, IExport
  {
    private static readonly Serilog.ILogger _log = Serilog.Log.ForContext("SourceContext", "DwgExporter");

    private const int sheetSizeCorrection = 1;

    public override string SaveFileDialogFilter => "Dwg files (*.dwg)|*.dwg";

    public async Task Export(Stream stream, ISheetPlacement sheetPlacement, bool doMergeLines, bool differentiateChildren)
    {
      await Export(stream, sheetPlacement.PolygonsForExport, sheetPlacement.Sheet, doMergeLines, differentiateChildren);
    }

    public async Task Export(Stream stream, IEnumerable<INfp> polygons, ISheet sheet, bool doMergeLines, bool differentiateChildren)
    {
      CadDocument dwgDocument = GenerateDwgDocument(polygons, sheet, sheet.Id, doMergeLines, differentiateChildren);

      if (dwgDocument.Entities.Count > 0)
      {
        await Task.Run(() =>
        {
          using (DwgWriter writer = new DwgWriter(stream, dwgDocument))
          {
            writer.Write();
          }
        }).ConfigureAwait(false);
      }
    }

    protected async override Task Export(string path, IEnumerable<INfp> polygons, IEnumerable<ISheet> sheets, bool doMergeLines, bool differentiateChildren)
    {
      try
      {
        Dictionary<CadDocument, int> dwgExports = new Dictionary<CadDocument, int>();
        var sheetList = sheets.ToList();

        for (var i = 0; i < sheets.Count(); i++)
        {
          var sheet = sheetList[i];
          CadDocument sheetDwg = GenerateDwgDocument(polygons, sheet, i, doMergeLines, differentiateChildren);
         
          dwgExports.Add(sheetDwg, sheet.Id);
        }

        for (var i = 0; i < dwgExports.Count(); i++)
        {
          var dwg = dwgExports.ElementAt(i).Key;
          var id = dwgExports.ElementAt(i).Value;

          if (dwg.Entities.Count > 0)
          {
            FileInfo fi = new FileInfo(path);
            await Task.Run(() =>
            {
              string outputPath = dwgExports.Count() == 1
                ? fi.FullName
                : $"{fi.FullName.Substring(0, fi.FullName.Length - 4)}{id}.dwg";

              using (DwgWriter writer = new DwgWriter(outputPath, dwg))
              {
                writer.Write();
              }
            }).ConfigureAwait(false);
          }
        }
      }
      catch (Exception ex)
      {
        throw;
      }
    }

    private static CadDocument GenerateDwgDocumentWithSheetOutline(ISheet origSheet, int sheetSizeDecreasement)
    {
      ISheet sheet = ((Sheet) origSheet).Clone();
      var maxY = sheet.Points.Max(p => p.Y);
      var minY = sheet.Points.Min(p => p.Y);
      var maxX = sheet.Points.Max(p => p.X);
      var minX = sheet.Points.Min(p => p.X);
      const double tol = 1e-6;

      foreach (var p in sheet.Points)
      {
        if (Math.Abs(p.Y - maxY) <= tol)
        {
          p.Y -= sheetSizeDecreasement / 2.0;
        }

        if (Math.Abs(p.Y - minY) <= tol)
        {
          p.Y += sheetSizeDecreasement / 2.0;
        }

        if (Math.Abs(p.X - maxX) <= tol)
        {
          p.X -= sheetSizeDecreasement / 2.0;
        }

        if (Math.Abs(p.X - minX) <= tol)
        {
          p.X += sheetSizeDecreasement / 2.0;
        }
      }

      var cadDocument = new CadDocument();
      cadDocument.Header.InsUnits = UnitsType.Millimeters;

      var sheetLayer = new Layer($"Plate_H{sheet.HeightCalculated}_W{sheet.WidthCalculated}")
      {
        Color = ACadSharp.Color.Magenta
      };
      cadDocument.Layers.Add(sheetLayer);

      var vertices = new List<XY>
      {
        new XY(0, 0),
        new XY(sheet.WidthCalculated, 0),
        new XY(sheet.WidthCalculated, sheet.HeightCalculated),
        new XY(0, sheet.HeightCalculated),
      };

      var sheetOutline = new LwPolyline();
      foreach (var vertex in vertices)
      {

        sheetOutline.Vertices.Add(new LwPolyline.Vertex { Location = vertex });
      }
      sheetOutline.IsClosed = true;
      sheetOutline.Layer = sheetLayer;

      cadDocument.Entities.Add(sheetOutline);
      return cadDocument;
    }

    private static IEnumerable<Entity> GetOffsetDwgEntities(IEnumerable<INfp> polygons, ISheet sheet, int i, bool differentiateChildren)
    {
      foreach (var polygon in polygons)
      {
        RawDetail<Entity> fl;
        // Check for .dxf files instead of .dwg
        if (polygon.Fitted == false || !polygon.Name.ToLower().Contains(".dxf") || polygon.Sheet.Id != sheet.Id)
        {
          continue;
        }
        else
        {
          try
          {
            // Use ACadSharp's DxfReader to load the original DXF file.
            CadDocument dxfDocument = DxfReader.Read(polygon.Name);

            // Use the existing DwgParser to process the entities from the loaded DXF document.
            // Exclude text entities — they are forwarded separately with correct transformation.
            var allEntities = dxfDocument.Entities.ToArray();
            var geomEntities = allEntities
                .Where(e => !(e is ACadSharp.Entities.TextEntity) && !(e is ACadSharp.Entities.MText))
                .ToArray();
            _log.Debug("GetOffsetDwgEntities: '{File}' has {Total} entities, {Text} text excluded from geometry",
                polygon.Name, allEntities.Length, allEntities.Length - geomEntities.Length);
            fl = DwgParser.ConvertDwgToRawDetail(polygon.Name, geomEntities);
          }
          catch (Exception ex)
          {
            // Added the inner exception for better debugging.
            throw new FileNotFoundException($"The file {polygon.Name} could not be loaded. When exporting the original files that generated the nest are used for precision, instead of the copies rotated and manipulated potentially many times during the nest; degrading potentially their accuracy. It would be possible to load and store the original files but that'd take some effort...", ex);
          }
        }

        XYZ offsetDistance = new XYZ(polygon.X , polygon.Y, 0D);
        List<Entity> newList = OffsetToNest(fl.Outers, offsetDistance, polygon.Rotation, differentiateChildren);

        foreach (Entity ent in newList)
        {
          yield return ent;
        }
      }
    }


    private static List<Entity> OffsetToNest(IEnumerable<ILocalContour> contours, XYZ offsetDistance, double rotation, bool differentiateChildren)
    {
      var allEntities = new List<Entity>();
      foreach (var contour in contours)
      {
        if (contour is LocalContour<Entity> castContour)
        {
          if (differentiateChildren && castContour.IsChild)
          {
            foreach (var child in castContour.Entities)
            {
              child.Color = ACadSharp.Color.Red;
            }
          }
          allEntities.AddRange(castContour.Entities);
        }
      }
      return OffsetToNest(allEntities, offsetDistance, rotation);
    }

    private static List<Entity> OffsetToNest(IList<Entity> dwgEntities, XYZ offset, double rotationAngle)
    {
      var result = new List<Entity>();
      foreach (var entity in dwgEntities)
      {
        var transformedEntity = (Entity)entity.Clone();

        switch (transformedEntity.ObjectType)
        {
          case ObjectType.LINE:
            var line = (Line)transformedEntity;
            line.StartPoint = RotateLocation(rotationAngle, line.StartPoint) + offset;
            line.EndPoint = RotateLocation(rotationAngle, line.EndPoint) + offset;
            break;
          case ObjectType.LWPOLYLINE:
            var lwPoly = (LwPolyline)transformedEntity;
            for (int i = 0; i < lwPoly.Vertices.Count; i++)
            {
              var vertex = lwPoly.Vertices[i];
              var res = new XYZ(vertex.Location.X, vertex.Location.Y, 0) + offset;
              var rotated = RotateLocation(rotationAngle, res);
              vertex.Location = new XY(rotated.X, rotated.Y);
              lwPoly.Vertices[i] = vertex;
            }
            break;
          case ObjectType.CIRCLE:
            var circle = (Circle)transformedEntity;
            circle.Center = RotateLocation(rotationAngle, circle.Center) + offset;
            break;
          case ObjectType.ARC:
            var arc = (Arc)transformedEntity;
            arc.Center = RotateLocation(rotationAngle, arc.Center) + offset;
            arc.StartAngle += rotationAngle;
            arc.EndAngle += rotationAngle;
            break;
          case ObjectType.ELLIPSE:
            var ellipse = (Ellipse)transformedEntity;
            ellipse.Center = RotateLocation(rotationAngle, ellipse.Center) + offset;
            ellipse.EndPoint = RotateLocation(rotationAngle, ellipse.EndPoint);
            break;
          default:
            break;
        }
        result.Add(transformedEntity);
      }
      return result;
    }

    private static XYZ RotateLocation(double rotationAngle, XYZ pt)
    {
      var angleRad = rotationAngle * Math.PI / 180.0;
      double cos = Math.Cos(angleRad);
      double sin = Math.Sin(angleRad);
      double x = pt.X * cos - pt.Y * sin;
      double y = pt.X * sin + pt.Y * cos;
      return new XYZ(x, y, pt.Z);
    }

    private CadDocument GenerateDwgDocument(IEnumerable<INfp> polygons, ISheet sheet, int i, bool doMergeLines, bool differentiateChildren)
    {
      try
      {
        var cadDocument = GenerateDwgDocumentWithSheetOutline(sheet, sheetSizeCorrection);
        var entities = GetOffsetDwgEntities(polygons.Where(o => o.Sheet.Id == sheet.Id), sheet, i, differentiateChildren);

        if (doMergeLines)
        {
          // TODO: Implement DWG-specific line merging if needed
        }

        foreach (var entity in entities)
        {
          cadDocument.Entities.Add(entity);
        }

        AddUrsaIdLabels(cadDocument, polygons, sheet, i);

        return cadDocument;
      }
      catch (Exception ex)
      {
        throw;
      }
    }

    /// <summary>Extension of the sidecar file (written by the add-in) that carries the URSA_ID label.</summary>
    private const string UrsaIdSidecarExtension = ".ursaid";

    private static void AddUrsaIdLabels(CadDocument cadDocument, IEnumerable<INfp> polygons, ISheet sheet, int sheetIndex)
    {
      Layer ursaIdLayer = null;
      int totalAdded = 0;

      foreach (var polygon in polygons)
      {
        if (polygon.Fitted == false || !polygon.Name.ToLower().Contains(".dxf") || polygon.Sheet.Id != sheet.Id)
          continue;

        try
        {
          // The label travels in a sidecar file next to the raw DXF rather than as a text
          // entity inside it: injecting text via IxMilia and re-saving produced a DXF whose
          // LINE geometry ACadSharp could not read, which wiped the part outlines from the
          // final sheet. Reading from the sidecar keeps the raw DXF a pristine Revit export.
          string sidecarPath = polygon.Name + UrsaIdSidecarExtension;
          if (!System.IO.File.Exists(sidecarPath))
          {
            _log.Debug("AddUrsaIdLabels: no sidecar for '{File}'", polygon.Name);
            continue;
          }

          var lines = System.IO.File.ReadAllLines(sidecarPath);
          if (lines.Length < 3 || string.IsNullOrEmpty(lines[0]))
          {
            _log.Debug("AddUrsaIdLabels: malformed sidecar '{File}'", sidecarPath);
            continue;
          }

          string label = lines[0];
          if (!double.TryParse(lines[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double cx) ||
              !double.TryParse(lines[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double cy))
          {
            _log.Debug("AddUrsaIdLabels: unparsable centroid in sidecar '{File}'", sidecarPath);
            continue;
          }

          double height = 30.0;
          if (lines.Length >= 4)
            double.TryParse(lines[3], NumberStyles.Float, CultureInfo.InvariantCulture, out height);

          if (ursaIdLayer == null)
          {
            if (cadDocument.Layers.Contains("URSA_ID"))
            {
              ursaIdLayer = cadDocument.Layers["URSA_ID"];
            }
            else
            {
              ursaIdLayer = new Layer("URSA_ID") { Color = new ACadSharp.Color(1) }; // red
              cadDocument.Layers.Add(ursaIdLayer);
            }
          }

          XYZ offsetDistance = new XYZ(polygon.X, polygon.Y, 0D);
          XYZ rotatedPt = RotateLocation(polygon.Rotation, new XYZ(cx, cy, 0)) + offsetDistance;

          cadDocument.Entities.Add(new ACadSharp.Entities.TextEntity
          {
            Value = label,
            InsertPoint = new XYZ(rotatedPt.X, rotatedPt.Y, 0),
            Height = height,
            Rotation = polygon.Rotation * Math.PI / 180.0,
            Layer = ursaIdLayer,
            Color = new ACadSharp.Color(1), // red — explicit so it's visible regardless of layer defaults
          });
          totalAdded++;
          _log.Debug("AddUrsaIdLabels: added label '{Value}' at ({X:F1},{Y:F1})",
              label, rotatedPt.X, rotatedPt.Y);
        }
        catch (Exception ex)
        {
          _log.Warning(ex, "AddUrsaIdLabels failed for '{File}'", polygon.Name);
        }
      }

      _log.Debug("AddUrsaIdLabels: sheet {SheetIndex} — {Total} label(s) added", sheetIndex, totalAdded);
    }
  }

  public static class DwgParser
  {
    public static RawDetail<Entity> ConvertDwgToRawDetail(string fileName, Entity[] entities)
    {
      var rawDetail = new RawDetail<Entity>();
      var points = ExtractPointsFromEntities(entities);
      var entitiesSet = new HashSet<Entity>(entities);
      var contour = new LocalContour<Entity>(points, entitiesSet);
      rawDetail.AddContour(contour);
      return rawDetail;
    }

    private static List<PointF> ExtractPointsFromEntities(IEnumerable<Entity> entities)
    {
      var points = new List<PointF>();
      foreach (var entity in entities)
      {
        switch (entity.ObjectType)
        {
          case ObjectType.LINE:
            var line = (Line)entity;
            points.Add(new PointF((float)line.StartPoint.X, (float)line.StartPoint.Y));
            points.Add(new PointF((float)line.EndPoint.X, (float)line.EndPoint.Y));
            break;
          case ObjectType.LWPOLYLINE:
            var lwPoly = (LwPolyline)entity;
            foreach (var vertex in lwPoly.Vertices)
            {
              points.Add(new PointF((float)vertex.Location.X, (float)vertex.Location.Y));
            }
            break;
          case ObjectType.CIRCLE:
            var circle = (Circle)entity;
            points.Add(new PointF((float)(circle.Center.X + circle.Radius), (float)circle.Center.Y));
            points.Add(new PointF((float)(circle.Center.X - circle.Radius), (float)circle.Center.Y));
            break;
        }
      }
      return points;
    }
  }
}
