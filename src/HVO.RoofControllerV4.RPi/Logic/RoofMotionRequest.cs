using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Logic;

/// <summary>An Open or Close for <see cref="IRoofControllerServiceV4.StartMotionAsync"/>: the direction and whose it is.</summary>
/// <param name="Direction">Opening or Closing.</param>
public sealed record RoofMotionRequest(RoofMotionDirection Direction)
{
    /// <summary>Who the motion will belong to once admitted.</summary>
    public RoofMotionOwner Owner { get; init; } = RoofMotionOwner.Unleased;

    /// <summary>
    /// The Alpaca command's own checks, made under the controller lock in the order #220's rules give; null for an
    /// ordinary command.
    /// </summary>
    internal IRoofCommandAdmission? Admission { get; init; }
}

/// <summary>
/// An Alpaca command's checks, called by the controller under its lock (the one that starts the relays) in the order of
/// #220's rules: <see cref="CheckGate"/> (step 1), the controller's hard protections (step 2), <see cref="CheckHumanControl"/>
/// (step 3), the repeat and no-op rules, <see cref="CheckDeadline"/>, the use-up point (<see cref="UseGrant"/>),
/// <see cref="CheckGeneration"/>, the controller's operation-in-progress check, the reversal rule, then
/// <see cref="CheckPolicy"/>. A waiting start calls <see cref="Recheck"/> again before its relay. Every method is quick:
/// no I/O, no wait, and it never calls back into the controller.
/// </summary>
internal interface IRoofCommandAdmission
{
    /// <summary>Step 1: commands allowed for this kind, the account's authorization unchanged, the ticket this instance's.</summary>
    RoofControllerException? CheckGate();

    /// <summary>Step 3: no person holds the operator lease, and the human Stop hold is clear.</summary>
    RoofControllerException? CheckHumanControl();

    /// <summary>
    /// Whether the command's answer still fits its deadline when the start must first wait <paramref name="wait"/> (the
    /// direction-change delay still to run). Checked before the use-up point, so a refusal here uses no grant.
    /// </summary>
    RoofControllerException? CheckDeadline(TimeSpan wait);

    /// <summary>The use-up point: any override grant of this command's kind is used up now, whatever follows.</summary>
    void UseGrant();

    /// <summary>Superseded when the controller's command generation moved since the ticket recorded it.</summary>
    RoofControllerException? CheckGeneration(long generation);

    /// <summary>
    /// The fresh-evidence guard, then the command's own policy judged now, and whether the grant used covers every reason
    /// that is not safe.
    /// </summary>
    RoofControllerException? CheckPolicy();

    /// <summary>
    /// A waiting start's continuation, before its relay: everything above again (the gate, the human lease and hold, the
    /// deadline, the policy with ages judged now, the grant's cover), with no new re-read.
    /// </summary>
    RoofControllerException? Recheck();

    /// <summary>Told when a waiting start was refused at its continuation (the roof stays stopped), for the alert.</summary>
    void ContinuationRefused(RoofControllerException refusal);
}
