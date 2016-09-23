﻿using System;
using System.Collections.Generic;
using System.Text;
using ServiceStack.Text;
using System.Drawing;
using System.Drawing.Imaging;
using OpenMetaverse;
using System.IO;
using System.Drawing.Drawing2D;

namespace InWorldz.PrimExporter.ExpLib.ImportExport
{
    /// <summary>
    /// Formatter that outputs results suitable for using with three.js
    /// </summary>
    public class BabylonJSONFormatter : IExportFormatter
    {
        public ExportResult Export(GroupDisplayData datas)
        {
            ExportResult result = new ExportResult();
            Dictionary<UUID, TrackedMaterial> materialTracker = new Dictionary<UUID, TrackedMaterial>();
            string tempPath = Path.GetTempPath();

            foreach (var data in datas.Prims)
            {
                result.Combine(ExportSingle(data, materialTracker, UUID.Random(), "png", result.TextureFiles, tempPath));
            }

            result.ObjectName = datas.ObjectName;
            result.CreatorName = datas.CreatorName;

            return result;
        }

        public ExportResult Export(PrimDisplayData data)
        {
            Dictionary<UUID, TrackedMaterial> materialTracker = new Dictionary<UUID, TrackedMaterial>();
            List<string> fileRecord = new List<string>();
            string tempPath = Path.GetTempPath();

            ExportResult result = ExportSingle(data, materialTracker, UUID.Random(), "png", fileRecord, tempPath);

            result.TextureFiles = fileRecord;

            return result;
        }

        /// <summary>
        /// Writes the given material texture to a file and writes back to the KVP whether it contains alpha
        /// </summary>
        /// <param name="textureAssetId"></param>
        /// /// <param name="textureName"></param>
        /// <param name="fileRecord"></param>
        /// <param name="tempPath"></param>
        /// <returns></returns>
        private KeyValuePair<UUID, TrackedMaterial> WriteMaterialTexture(UUID textureAssetId, string textureName, string tempPath, List<string> fileRecord)
        {
            const int MAX_IMAGE_SIZE = 512;

            Image img = null;
            bool hasAlpha = false;
            if (GroupLoader.Instance.LoadTexture(textureAssetId, ref img, false))
            {
                img = ConstrainTextureSize((Bitmap)img, MAX_IMAGE_SIZE);
                hasAlpha = DetectAlpha((Bitmap)img);
                string fileName = Path.Combine(tempPath, textureName);

                using (img)
                {
                    img.Save(fileName, ImageFormat.Png);
                }

                fileRecord.Add(fileName);
            }

            KeyValuePair<UUID, TrackedMaterial> retMaterial = new KeyValuePair<UUID, TrackedMaterial>(textureAssetId,
                new TrackedMaterial { HasAlpha = hasAlpha, Name = textureName });

            return retMaterial;
        }

        private Image ConstrainTextureSize(Bitmap img, int size)
        {
            if (img.Width > size)
            {
                Image thumbNail = new Bitmap(size, size, img.PixelFormat);
                using (Graphics g = Graphics.FromImage(thumbNail))
                {
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    Rectangle rect = new Rectangle(0, 0, size, size);
                    g.DrawImage(img, rect);
                }

                img.Dispose();
                return thumbNail;
            }

            return img;
        }

        private bool DetectAlpha(Bitmap img)
        {
            for (int x = 0; x < img.Width; x++)
            {
                for (int y = 0; y < img.Height; y++)
                {
                    Color c = img.GetPixel(x, y);
                    if (c.A < 255) return true;
                }
            }

            return false;
        }

        private ExportResult ExportSingle(PrimDisplayData data, Dictionary<UUID, TrackedMaterial> materialTracker, UUID index,
            string materialType, List<string> textureFileRecord, string tempPath)
        {
            ExportResult result = new ExportResult();

            List<OpenMetaverse.Primitive.TextureEntryFace> materials;
            string jsonString = this.SerializeCombinedFaces(index, data, out materials, materialTracker, materialType, textureFileRecord, tempPath);

            result.FaceBytes = new List<byte[]>();
            result.FaceBytes.Add(Encoding.UTF8.GetBytes(jsonString));

            result.BaseObjects.Add(data);

            return result;
        }

        private string SerializeCombinedFaces(
            UUID index, PrimDisplayData data, out List<OpenMetaverse.Primitive.TextureEntryFace> materials, Dictionary<UUID, TrackedMaterial> materialTracker,
            string materialType, List<string> textureFileRecord, string tempPath)
        {
            BabylonJSONPrimFaceCombiner combiner = new BabylonJSONPrimFaceCombiner();
            foreach (var face in data.Mesh.Faces)
            {
                combiner.CombineFace(face);
            }
            

            var meta = new
            {
                formatVersion = 3.1,
                generatedBy = "InWorldz.PrimExporter",
                vertices = combiner.Vertices.Count,
                //faces = combiner.TotalFaces,
                normals = combiner.Normals.Count / 3,
                colors = 0,
                uvs = combiner.UVs.Count / 2,
                materials = combiner.Materials.Count,
                morphTargets = 0,
                bones = 0
            };

            List<object> jsMaterials = new List<object>();
            for (int i = 0; i < combiner.Materials.Count; i++)
            {
                var material = combiner.Materials[i];
                float shinyPercent = ShinyToPercent(material.Shiny);

                bool hasMaterial = material.TextureID != OpenMetaverse.UUID.Zero;

                //check the material tracker, if we already have this texture, don't export it again
                TrackedMaterial trackedMaterial = null;

                if (hasMaterial)
                {
                    if (materialTracker.ContainsKey(material.TextureID))
                    {
                        trackedMaterial = materialTracker[material.TextureID];
                    }
                    else
                    {
                        string materialMapName = String.Format("tex_mat_{0}_{1}." + materialType, index.ToString(), i);
                        var kvp = this.WriteMaterialTexture(material.TextureID, materialMapName, tempPath, textureFileRecord);

                        materialTracker.Add(kvp.Key, kvp.Value);

                        trackedMaterial = kvp.Value;
                    }
                }

                bool hasTransparent = material.RGBA.A < 1.0f || (trackedMaterial != null && trackedMaterial.HasAlpha);

                var jsMaterial = new
                {
                    colorAmbient = new float[] { material.RGBA.R, material.RGBA.G, material.RGBA.B },
                    colorDiffuse = new float[] { material.RGBA.R, material.RGBA.G, material.RGBA.B },
                    colorSpecular = new float[] { material.RGBA.R * shinyPercent, material.RGBA.G * shinyPercent, material.RGBA.B * shinyPercent },
                    mapDiffuse = hasMaterial ? trackedMaterial.Name : null,
                    mapDiffuseWrap = hasMaterial ? new string[] { "repeat", "repeat" } : null,
                    shading = "Phong",
                    specularCoef = 50,
                    transparency = material.RGBA.A,
                    transparent = hasTransparent ? true : false,
                };
                jsMaterials.Add(jsMaterial);

            }

            var body = new
            {
                metadata = meta,
                scale = 1.0,
                materials = jsMaterials,
                vertices = combiner.Vertices,
                morphTargets = new int[0],
                normals = combiner.Normals,
                colors = new float[0],
                uvs = new float[][] { combiner.UVs.ToArray() },
                //faces = combiner.EncodedIndices
            };

            materials = combiner.Materials;

            return JsonSerializer.SerializeToString(body);
        }

        private float ShinyToPercent(OpenMetaverse.Shininess shininess)
        {
            switch (shininess)
            {
                case OpenMetaverse.Shininess.High:
                    return 1.0f;
                case OpenMetaverse.Shininess.Medium:
                    return 0.5f;
                case OpenMetaverse.Shininess.Low:
                    return 0.25f;
            }

            return 0.0f;
        }


    }
}