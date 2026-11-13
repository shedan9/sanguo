using System.Collections.Immutable;
using Sanguo.BattleCore.Randomness;

namespace Sanguo.BattleCore.Basic;

public enum BasicCardKind { Slash, Dodge, Peach, Duel, ExNihilo, Nullification, Dismantlement, Snatch }
public enum BattlePhase { Play, Discard, Finished }
public enum BattleActionKind { Slash, Dodge, Peach, Pass, EndPlay, Discard, Duel, ExNihilo, RespondSlash, Nullify, Dismantlement, Snatch, SelectHandCard, InvokeSkill }
public enum EffectKind { FinishCard, Dodge, Damage, Rescue, Duel, DrawCards, Nullification, SelectHandCard, AfterDamage, InvokeJianxiong }

public sealed record BattleCard(string InstanceId, BasicCardKind Kind, string Suit, int Rank);
public sealed record CombatantSetup(string EntityId, string ControllerId, string TeamId, int MaxHp = 4,
    BattleProfile Profile = BattleProfile.Generic);
public sealed record Combatant(string EntityId, string ControllerId, string TeamId, int SeatId,
    int Hp, int MaxHp, bool Alive, ImmutableArray<BattleCard> Hand, BattleProfile Profile = BattleProfile.Generic);
public sealed record BattleAction(BattleActionKind Kind, string? CardId = null, string? TargetId = null,
    string? ResponseToCardId = null, int? HandIndex = null, string? SkillId = null, string? CostCardId = null);
public sealed record EffectFrame(EffectKind Kind, string SourceId, string TargetId, string CardId,
    int Amount = 0, int Cursor = 0, string? CounteredCardId = null, int ChainDepth = 0,
    string? SkillId = null, int DamageBonus = 0);
public sealed record BattleDecision(string RequestId, string ControllerId, string ActorId);
public sealed record BattleEvent(long Sequence, string Code, string ActorId, string? TargetId = null,
    string? CardId = null, int Amount = 0, string? RelatedCardId = null);
public sealed record BattleState(string BattleId, string RulesetId, long StateVersion,
    ImmutableArray<Combatant> Combatants, ImmutableArray<BattleCard> DrawPile,
    ImmutableArray<BattleCard> Processing, ImmutableArray<BattleCard> DiscardPile,
    ImmutableArray<string> CardInventory, RandomState Random, int TurnCursor, int TurnNumber,
    BattlePhase Phase, int SlashesUsed, ImmutableArray<EffectFrame> Frames,
    BattleDecision? PendingDecision, string? WinnerTeamId, ImmutableArray<BattleEvent> Events,
    bool FenjiUsed = false);

public sealed record CombatantView(string EntityId, string TeamId, int SeatId, int Hp, int MaxHp,
    bool Alive, int HandCount, ImmutableArray<BattleCard> VisibleHand,
    BattleProfile Profile = BattleProfile.Generic, ImmutableArray<BattleSkillView> Skills = default);
public sealed record BattleResponse(EffectKind Kind, string SourceId, string TargetId, string CardId, int ChainDepth,
    string? SkillId = null, int DamageBonus = 0);
public sealed record BattleView(string BattleId, long StateVersion, BattlePhase Phase, int TurnNumber,
    string ActiveUnitId, ImmutableArray<CombatantView> Combatants, int DrawCount,
    ImmutableArray<BattleCard> Processing, ImmutableArray<BattleCard> DiscardPile,
    BattleDecision? PendingDecision, ImmutableArray<BattleAction> LegalActions, string? WinnerTeamId,
    BattleResponse? Response = null);
