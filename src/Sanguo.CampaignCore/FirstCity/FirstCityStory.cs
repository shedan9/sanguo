using System.Collections.Immutable;

namespace Sanguo.CampaignCore.FirstCity;

public enum CampaignPhase { Choice, Deployment, Battle, Defeat, Complete }
public sealed record StoryChoice(string Id, string Text, string Consequence);
public sealed record StoryEvent(string Id, string Title, string Text, ImmutableArray<StoryChoice> Choices);
public sealed record CampaignChoice(int Stage, string ChoiceId);
public sealed record CampaignProgress(string CampaignId, Origin Origin, ulong Seed, int Stage, int Attempt,
    CampaignPhase Phase, ImmutableArray<string> Roster, ImmutableArray<CampaignChoice> Choices);

/// <summary>首城开发切片；独立于制作大纲及G0试验，不声明正式人物或完整技能已接入。</summary>
public static class FirstCityStory
{
    public const string Version = "campaign.first-city-development.v1";
    public const string Protagonist = "protagonist";
    public const string Volunteer = "companion.volunteer";
    public const string CityId = "city.liuping";
    public static string OriginName(Origin origin) => origin switch
    {
        Origin.Damage => "乡勇", Origin.Control => "寒门学子", Origin.Tank => "山野农户",
        _ => throw new ArgumentOutOfRangeException(nameof(origin))
    };
    public static string EncounterName(int stage) => stage switch
    {
        0 => "故乡撤离", 1 => "驿路通行", 2 => "柳平外城", 3 => "柳平内城",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
    public static StoryEvent Event(Origin origin, int stage)
    {
        if (!Enum.IsDefined(origin) || stage is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(stage));
        var (title, text, first, second, resultA, resultB) = (origin, stage) switch
        {
            (Origin.Damage, 0) => ("烽火中的乡路", "184年，村口火光逼近。妹妹阿榆尚未归队，何峻催你追上纵火者，伤者却走不动。眼前这一仗，是为追问，也是为护送。", "先护送伤者", "先截住追兵，再接伤者", "阿榆归队时听见伤者说，是你让队伍等了他们。", "何峻记住了你迎向追兵的背影，阿榆请你别忘了回头的人。"),
            (Origin.Damage, 1) => ("求援无门", "数年后，护乡队辗转来到柳平城外。官署只肯登记损失，不肯开门。何峻想直接报复，阿榆带回被裹挟者的消息。你决定先打通驿路，让求援者能够通行。", "留下求援者的共同证言", "先安顿伤者，再公开邀战", "一份共同证言留在驿亭，护乡队不再只替自己发声。", "伤者有了暂住处，你也公开承诺不把无辜者当作报复的代价。"),
            (Origin.Damage, 2) => ("城门之前", "柳平守军借围剿之名拒绝伤者入城。何峻要求立威，阿榆追问入城之后怎样约束自己的人。你必须依次赢下外城和内城牌局，才有能力兑现承诺。", "先救被扣乡民，再说明约束", "公开约束护乡队，再迎战", "被扣乡民回到队伍，何峻也须听取他们的陈述。", "护乡队留下公开约定，入城后仍接受乡民质询。"),
            (Origin.Control, 0) => ("名字在伪令上", "184年，一纸伪令牵连了无辜者，原稿上竟有你的署名。陈简催你带人离开，顾衡的门客要求先交出原稿。追索者已至，你只能先打通脱身的路。", "保住原稿，带人撤离", "先救被牵连者，留下证人", "原稿留存，你承诺有朝一日连自己的署名也一并公开。", "陈简带出了证人，他们记住的是你回来接人的举动。"),
            (Origin.Control, 1) => ("引荐的代价", "数年后，顾衡愿意引荐你，却要求删掉门客的责任。柳平官署拒收陈述。驿道被封，你决定以牌局打通通行，让不同说法都能送到城门前。", "保留顾衡门客的责任记录", "先保护陈述者，再补齐案卷", "旧恩不能抹掉署名，你把门客的陈述与责任一起保存。", "陈述者得到保护，陈简答应继续寻找缺失的证据。"),
            (Origin.Control, 2) => ("自己的案头", "柳平守将只肯听顾衡一方的文书。陈简问你，若赢得此城，是否也允许别人质询你。外城与内城两场牌局都获胜后，城中才能有一张公开申辩的案头。", "先公开自己的错误", "先保护证人，约定公开复核", "你的署名也进入案卷，公开申辩不再只针对败者。", "陈简为证人留席，复核期限也约束着你。"),
            (Origin.Tank, 0) => ("带谁上路", "184年，故乡失守。陶婶要等失散亲人，周禾催能走的人先走。追兵已到村口，你得用一场牌局争取撤离的路。", "先护送行动不便者", "先打通路线，再返回接人", "陶婶替你记下同行者的名字，队伍愿意一起等候。", "周禾看见你如约返回，留下的人终于跟上了队伍。"),
            (Origin.Tank, 1) => ("借住的期限", "数年后，几处借住都被收回。柳平只准你们在城外暂留，新来的旅人也无处可去。你决定先打通驿路，再寻一处不会任意驱逐人的归处。", "让新遇旅人一同暂住", "先安排独立落脚，再共同协商", "新来的旅人也有了名字，周禾开始听他们讲述自己的故乡。", "旅人有了落脚处，陶婶提醒你别让暂住又变成驱逐。"),
            _ => ("留下的资格", "柳平守军要求留下少数人，才肯让本乡人进城。周禾觉得值得，陶婶却不肯用旁人的离散换自己的安稳。你须依次赢下外城、内城，才能守住共同的归处。", "公开拒绝以他人交换容身", "先保全被扣者，再说明接纳", "城门的承诺同样留给后来者，先来不再等于可以排他。", "被扣者获救，陶婶询问下一批无家者能否也在这里落脚。")
        };
        var route = origin.ToString().ToLowerInvariant();
        return new($"first-city.{route}.{stage + 1:D2}", title, text,
            [new("a", first, resultA), new("b", second, resultB)]);
    }
    public static string Epilogue(CampaignProgress progress)
    {
        var opening = progress.Choices.Single(c => c.Stage == 0).ChoiceId;
        return progress.Origin switch
        {
            Origin.Damage => (opening == "a" ? "当初被护送的伤者在城中作证。" : "当初被你接回的人在城门重逢。") + "阿榆把最初的约定交给乡民：守一乡的刀，也要接受天下人的追问。",
            Origin.Control => (opening == "a" ? "最初的原稿放到了案头。" : "最初获救的证人来到案头。") + "陈简递来第一份申辩。你的名字也在需要回应的人之中。",
            Origin.Tank => (opening == "a" ? "陶婶重新点起了同行者的灯。" : "周禾为后来的人留了一盏归途的灯。") + "城门外又有人叩门。安身的承诺，从此也要回应他们。",
            _ => throw new ArgumentOutOfRangeException(nameof(progress))
        };
    }
}
