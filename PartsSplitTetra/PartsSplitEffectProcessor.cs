using OpenCvSharp;
using System.Numerics;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Json;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Effects;
using YukkuriMovieMaker.Settings;

namespace PartsSplitTetra
{
    internal class PartsSplitEffectProcessor : IVideoEffectProcessor
    {
        IGraphicsDevicesAndContext devices;

        PartsSplitEffect item;

        ID2D1Image? input;

        DisposeCollector disposer = new();

        AffineTransform2D transform2D;

        ID2D1CommandList? commandList;

        bool isFirst = true;
        float x, y;
        int oldPartsCount;
        string oldSerializedEffects = string.Empty;

        List<ID2D1Bitmap> oldBitmaps = new();

        List<VideoEffectChain> chains = new();

        public ID2D1Image Output { get; }

        public PartsSplitEffectProcessor(IGraphicsDevicesAndContext devices, PartsSplitEffect item)
        {
            this.devices = devices;
            this.item = item;

            transform2D = new AffineTransform2D(devices.DeviceContext);
            disposer.Collect(transform2D);

            Output = transform2D.Output;
            disposer.Collect(Output);
        }

        public DrawDescription Update(EffectDescription effectDescription)
        {
            if (input is null) throw new InvalidOperationException("input is null");

            var frame = effectDescription.ItemPosition.Frame;
            var length = effectDescription.ItemDuration.Frame;
            var fps = effectDescription.FPS;

            var threshold = Math.Clamp((int)item.Threshold.GetValue(frame, length, fps), 0, 255);
            var drawingOrder = item.DrawingOrder;
            var videoEffects = item.VideoEffects;

            var bounds = devices.DeviceContext.GetImageLocalBounds(input);

            var parts = Split(input, bounds, threshold, drawingOrder, devices.DeviceContext);

            var partsCount = parts.Count;
            var serializedEffects = Json.GetJsonText(videoEffects);

            // 前回の描画で使ったBitmapを破棄する
            foreach (var bitmap in oldBitmaps)
            {
                bitmap.Dispose();
            }
            oldBitmaps.Clear();

            if (isFirst || oldPartsCount != partsCount || oldSerializedEffects != serializedEffects)
            {
                foreach (var chain in chains)
                {
                    chain.Dispose();
                }
                chains.Clear();

                for (int i = 0; i < partsCount; i++)
                {
                    var partVideoEffects = new List<IVideoEffect>();
                    foreach (var effect in videoEffects)
                    {
                        partVideoEffects.Add(effect);
                    }

                    var chain = new VideoEffectChain(devices);
                    chain.VideoEffects = partVideoEffects;
                    chains.Add(chain);
                }
            }

            EffectDescription CreateDesc(Vector2 position, int index)
            {
                var drawDesc = effectDescription.DrawDescription with
                {
                    Draw = new Vector3(position.X, position.Y, 0),
                    CenterPoint = new Vector2(0),
                    Zoom = new Vector2(1),
                    Rotation = new Vector3(0),
                    Camera = Matrix4x4.Identity,
                    ZoomInterpolationMode = (Vortice.Direct2D1.InterpolationMode)SettingsBase<YMMSettings>.Default.GetZoomMode(),
                    Opacity = 1,
                    Invert = false,
                    Controllers = [],
                };

                return effectDescription with
                {
                    DrawDescription = drawDesc,
                    InputCount = partsCount,
                    InputIndex = index,
                };
            }

            var chainOutputs = new List<ID2D1Image>();

            for (int i = 0; i < partsCount; i++)
            {
                var bitmap = parts[i].Bitmap;
                oldBitmaps.Add(bitmap);

                var position = parts[i].Position;

                var chain = chains[i];

                chain.SetInput(bitmap);
                chain.Update(CreateDesc(position, i));
                chainOutputs.Add(chain.Output);
            }

            if (commandList is not null)
            {
                disposer.RemoveAndDispose(ref commandList);
            }
            var dc = devices.DeviceContext;
            commandList = dc.CreateCommandList();
            disposer.Collect(commandList);

            dc.Target = commandList;
            dc.BeginDraw();
            dc.Clear(null);

            foreach (var image in chainOutputs)
            {
                dc.DrawImage(image);
            }

            dc.EndDraw();
            dc.Target = null;
            commandList.Close();

            transform2D.SetInput(0, commandList, true);

            var x = bounds.Left;
            var y = bounds.Top;

            if (isFirst || this.x != x || this.y != y)
            {
                transform2D.TransformMatrix = Matrix3x2.CreateTranslation(x, y);
                this.x = x;
                this.y = y;
            }
            
            isFirst = false;
            oldPartsCount = partsCount;
            oldSerializedEffects = serializedEffects;
            return effectDescription.DrawDescription;
        }

        private static List<(ID2D1Bitmap Bitmap, Vector2 Position)> Split(ID2D1Image input, RawRectF bounds, int threshold, DrawingOrder drawingOrder, ID2D1DeviceContext dc)
        {
            using var source = ToMat(input, bounds, dc);

            if (source is null) return [];

            var channels = source.Split();
            channels[0].Dispose();
            channels[1].Dispose();
            channels[2].Dispose();
            using var alpha = channels[3];

            using var binary = new Mat();
            Cv2.Threshold(alpha, binary, threshold, 255, ThresholdTypes.Binary);

            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            var labelCount = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids);

            var result = new List<(ID2D1Bitmap Part, Vector2 Position)>();

            // ラベル0は背景なのでi = 1からスタート
            for (int i = 1; i < labelCount; i++)
            {
                var bx = stats.At<int>(i, (int)ConnectedComponentsTypes.Left);
                var by = stats.At<int>(i, (int)ConnectedComponentsTypes.Top);
                var bw = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
                var bh = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);

                var roi = new OpenCvSharp.Rect(bx, by, bw, bh);

                using var mask = new Mat();
                Cv2.Compare(labels, new Scalar(i), mask, CmpType.EQ);

                using var roiMask = new Mat(mask, roi);

                using var blob = new Mat(bh, bw, MatType.CV_8UC4, new Scalar(0, 0, 0, 0));
                using var roiSource = new Mat(source, roi);
                roiSource.CopyTo(blob, roiMask);

                result.Add((ToID2D1Bitmap(blob, dc), new Vector2(bx, by)));
            }

            if (drawingOrder == DrawingOrder.Left)
            {
                result.Sort((a, b) => a.Position.X.CompareTo(b.Position.X));
            }
            if (drawingOrder == DrawingOrder.Right)
            {
                result.Sort((a, b) => b.Position.X.CompareTo(a.Position.X));
            }
            if (drawingOrder == DrawingOrder.Top)
            {
                result.Sort((a, b) => a.Position.Y.CompareTo(b.Position.Y));
            }
            if (drawingOrder == DrawingOrder.Bottom)
            {
                result.Sort((a, b) => b.Position.Y.CompareTo(a.Position.Y));
            }

            return result;
        }

        private static Mat? ToMat(ID2D1Image image, RawRectF bounds, ID2D1DeviceContext dc)
        {
            var width = (int)(bounds.Right - bounds.Left);
            var height = (int)(bounds.Bottom - bounds.Top);

            if (width == 0 || height == 0) return null;

            using var targetBitmap = CreateBitmap(width, height, BitmapOptions.Target, dc);

            dc.Target = targetBitmap;
            dc.BeginDraw();
            dc.Clear(null);
            dc.DrawImage(image, new Vector2(-bounds.Left, -bounds.Top));
            dc.EndDraw();
            dc.Target = null;

            using var cpuBitmap = CreateBitmap(width, height, BitmapOptions.CpuRead | BitmapOptions.CannotDraw, dc);
            cpuBitmap.CopyFromBitmap(targetBitmap);

            var mapped = cpuBitmap.Map(MapOptions.Read);

            try
            {
                using var mat = new Mat(cpuBitmap.PixelSize.Height, cpuBitmap.PixelSize.Width, MatType.CV_8UC4, mapped.Bits, mapped.Pitch);
                return mat.Clone();
            }
            finally
            {
                cpuBitmap.Unmap();
            }
        }

        private static ID2D1Bitmap1 CreateBitmap(int width, int height, BitmapOptions bitmapOptions, ID2D1DeviceContext dc)
        {
            var bitmapProperties = new BitmapProperties1()
            {
                PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied),
                DpiX = 96f,
                DpiY = 96f,
                BitmapOptions = bitmapOptions,
                ColorContext = null
            };

            return dc.CreateBitmap(new SizeI(width, height), bitmapProperties);
        }

        private static ID2D1Bitmap ToID2D1Bitmap(Mat mat, ID2D1DeviceContext dc)
        {
            if (mat.Empty()) return dc.CreateEmptyBitmap();

            var pixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied);
            var bitmapProperties = new BitmapProperties(pixelFormat);

            return dc.CreateBitmap(new SizeI(mat.Width, mat.Height), mat.Data, (int)mat.Step(), bitmapProperties);
        }

        public void SetInput(ID2D1Image? input)
        {
            this.input = input;
        }

        public void ClearInput()
        {
            transform2D.SetInput(0, null, true);
        }

        public void Dispose()
        {
            foreach (var bitmap in oldBitmaps)
            {
                bitmap.Dispose();
            }
            oldBitmaps.Clear();

            foreach (var chain in chains)
            {
                chain.Dispose();
            }
            chains.Clear();

            disposer.Dispose();
        }
    }
}