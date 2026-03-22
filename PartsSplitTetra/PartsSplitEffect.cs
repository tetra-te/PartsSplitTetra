using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;

namespace PartsSplitTetra
{
    [VideoEffect("パーツ分解てとら", [], ["parts split tetra"], isEffectItemSupported: false, isAviUtlSupported: false)]
    internal class PartsSplitEffect : VideoEffectBase
    {
        public override string Label => "パーツ分解てとら";

        [Display(GroupName = "パーツ分解てとら", Name = "しきい値", Description = "不透明度のしきい値")]
        [AnimationSlider("F0", "", 0, 255)]
        public Animation Threshold { get; } = new Animation(128, 0, 255);

        [Display(GroupName = "パーツ分解てとら", Name = "描画順", Description = "各パーツを描画する順番")]
        [EnumComboBox]
        public DrawingOrder DrawingOrder { get; set => Set(ref field, value); } = DrawingOrder.Left;

        [Display(GroupName = "パーツ分解てとら / 映像エフェクト", Name = "", Description = "")]
        [VideoEffectSelector]
        public ImmutableList<IVideoEffect> VideoEffects { get; set => Set(ref field, value); } = [];

        public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription)
        {
            return [];
        }

        public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        {
            return new PartsSplitEffectProcessor(devices, this);
        }

        protected override IEnumerable<IAnimatable> GetAnimatables()
        {
            return [Threshold, ..VideoEffects];
        }
    }

    internal enum DrawingOrder
    {
        [Display(Name = "左から")]
        Left,
        [Display(Name = "右から")]
        Right,
        [Display(Name = "上から")]
        Top,
        [Display(Name = "下から")]
        Bottom,
    }
}
