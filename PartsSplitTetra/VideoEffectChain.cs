using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;
using YukkuriMovieMaker.Plugin.Effects;

namespace PartsSplitTetra
{
    internal class VideoEffectChain : IDisposable
    {
        IGraphicsDevicesAndContext devices;

        ID2D1Image? input;

        List<IVideoEffect>? oldVideoEffects;

        Dictionary<IVideoEffect, IVideoEffectProcessor> processors = new();

        public List<IVideoEffect>? VideoEffects { get; set; }

        DisposeCollector disposer = new();

        AffineTransform2D wrap;

        DrawingEffect drawingEffect;

        public ID2D1Image Output { get; }

        public VideoEffectChain(IGraphicsDevicesAndContext devices)
        {
            this.devices = devices;

            wrap = new AffineTransform2D(devices.DeviceContext);
            disposer.Collect(wrap);

            Output = wrap.Output;
            disposer.Collect(Output);

            drawingEffect = new DrawingEffect(devices);
            disposer.Collect(drawingEffect);
        }

        public void Update(EffectDescription effectDescription)
        {
            if (VideoEffects is null) throw new InvalidOperationException("VideoEffects is null");

            var effects = new List<IVideoEffect>();
            foreach (var effect in VideoEffects)
            {
                if (!effect.IsEnabled) continue;
                effects.Add(effect);
            }

            if (oldVideoEffects is not null)
            {
                foreach (var oldVideoEffect in oldVideoEffects)
                {
                    if (!effects.Contains(oldVideoEffect) && processors.TryGetValue(oldVideoEffect, out var p))
                    {
                        p.ClearInput();
                        p.Dispose();
                        processors.Remove(oldVideoEffect);
                    }
                }
            }

            var image = input;
            var desc = effectDescription;

            foreach (var effect in effects)
            {
                IVideoEffectProcessor processor;
                if (processors.TryGetValue(effect, out var p))
                {
                    processor = p;
                }
                else
                {
                    processor = effect.CreateVideoEffect(devices);
                    processors[effect] = processor;
                }

                processor.SetInput(image);
                desc = desc with
                {
                    DrawDescription = processor.Update(desc)
                };
                
                image = processor.Output;
            }

            drawingEffect.SetInput(image);
            drawingEffect.Update(desc);
            image = drawingEffect.Output;

            wrap.SetInput(0, image, true);

            oldVideoEffects = effects;
        }

        public void SetInput(ID2D1Image? input)
        {
            this.input = input;
        }

        private void ClearInput()
        {
            wrap.SetInput(0, null, true);

            foreach (var processor in processors.Values)
            {
                processor.ClearInput();
            }
            drawingEffect.ClearInput();
        }

        public void Dispose()
        {
            ClearInput();

            disposer.Dispose();

            foreach (var processor in processors.Values)
            {
                processor.Dispose();
            }
        }
    }
}
