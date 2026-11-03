using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Player.CharacterController;
using Il2CppMonomiPark.SlimeRancher.Regions;
using Il2CppMonomiPark.SlimeRancher.Slime;
using JetBrains.Annotations;
using SR2MP.Packets.Actor;
using SR2MP.Shared.Managers;
using SR2MP.Shared.Utils;
using Starlight.Storage;

namespace SR2MP.Components.Actor;

[InjectIntoIL]
internal sealed partial class NetworkActor : MonoBehaviour
{
    public  RegionMember? RegionMember;
    private Identifiable identifiable;
    private ResourceCycle? cycle;
    private Rigidbody rigidbody;
    private SlimeEmotions emotions;
    private PlortModel? plortModel;

    public float SyncTimer = Timers.ActorTimer;
    public bool  ShouldUpdateResourceState;
    public bool  IsValid = true;
    public bool  IsDestroyed;
    public byte  AttemptedGetIdentifiable;
    public bool  CachedLocallyOwned;

    private Vector3 savedVelocity;
    private long registeredActorId;

    internal bool isSlime;
    internal bool isResource;
    internal bool isPlort;

    public ActorId ActorId
    {
        get
        {
            if (IsDestroyed)
            {
                IsValid = false;
                return new ActorId(0);
            }

            if (identifiable != null)
                return GetActorIdSafe();

            if (AttemptedGetIdentifiable >= 10)
            {
                SrLogger.LogWarning("Failed to get Identifiable after 10 attempts");
                IsValid = false;
                return new ActorId(0);
            }

            try
            {
                identifiable = GetComponent<Identifiable>();
                AttemptedGetIdentifiable++;
            }
            catch (Exception ex)
            {
                SrLogger.LogWarning($"Failed to get Identifiable component: {ex.Message}");
                AttemptedGetIdentifiable++;
                IsValid = false;
                return new ActorId(0);
            }

            return identifiable ? GetActorIdSafe() : new ActorId(0);
        }
    }

    private ActorId GetActorIdSafe()
    {
        try
        {
            return identifiable.GetActorId();
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Failed to get ActorId: {ex.Message}");
            IsValid = false;
            return new ActorId(0);
        }
    }

    /// <summary>Whether this machine runs the physics and AI of the actor. Set by the ownership manager.</summary>
    public bool LocallyOwned { get; set; }

    private bool IsHibernating => RegionMember && RegionMember!._hibernating;

    /// <summary>Loaded and not hibernating: only then can this machine simulate the actor.</summary>
    internal bool IsAwake => !IsDestroyed && gameObject.activeInHierarchy && !IsHibernating;

    /// <summary>The actor's speed, including the speed the game stored when it put the actor to sleep.</summary>
    internal Vector3 CurrentVelocity
    {
        get
        {
            if (IsHibernating)
                return RegionMember!._hibernatingVelocity;

            return rigidbody ? rigidbody.velocity : Vector3.zero;
        }
    }

    private void InitializeComponents()
    {
        emotions     = GetComponent<SlimeEmotions>();
        rigidbody    = GetComponent<Rigidbody>();
        identifiable = GetComponent<Identifiable>();
        cycle        = GetComponent<ResourceCycle>();
        RegionMember = GetComponent<RegionMember>();

        InitializeLabyrinthComponents();
    }

    public void Start()
    {
        try
        {
            if (GetComponent<Gadget>() || GetComponent<SRCharacterController>())
            {
                Destroy(this);
                return;
            }

            InitializeComponents();
            GetActorType();

            registeredActorId = ActorId.Value;
            ActorOwnershipManager.Register(this, registeredActorId);
            CachedLocallyOwned = LocallyOwned;

            SetRigidbodyState(LocallyOwned);
            ApplyLabyrinthSimulationGate();
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"NetworkActor.Start error: {ex}");
            IsValid = false;
        }
    }

    private void GetActorType()
    {
        if (ActorId.Value == 0 || !GameState.identifiables.TryGetValue(ActorId, out var identModel))
            return;

        isSlime    = identModel.TryCast<SlimeModel>()   != null;
        isResource = identModel.TryCast<ProduceModel>() != null;
        isPlort    = identModel.TryCast<PlortModel>()   != null;
    }

    public void Update()
    {
        if (IsDestroyed)
            return;

        if (!IsValid)
        {
            IsDestroyed = true;
            Destroy(this);
            return;
        }

        try
        {
            UpdateResourceState();
            HandleOwnershipChange();

            UpdateInterpolation();

            SyncTimer -= UnityEngine.Time.unscaledDeltaTime;

            if (SyncTimer >= 0)
                return;

            SyncTimer = Timers.ActorTimer;

            if (LocallyOwned)
            {
                SendStateUpdate();
                SendWorldUpdate();
            }
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"NetworkActor.Update error: {ex}");
            IsValid = false;
        }
    }

    private void HandleOwnershipChange()
    {
        if (CachedLocallyOwned != LocallyOwned)
        {
            SetRigidbodyState(LocallyOwned);
            ApplyLabyrinthSimulationGate();

            if (LocallyOwned && rigidbody)
                rigidbody.velocity = savedVelocity;

            if (LocallyOwned)
                RestoreStateOnOwnership();
        }

        CachedLocallyOwned = LocallyOwned;
    }

    private void SetRigidbodyState(bool locallyOwned)
    {
        if (rigidbody == null || IsDestroyed)
            return;

        try
        {
            rigidbody.constraints = locallyOwned
                ? RigidbodyConstraints.None
                : RigidbodyConstraints.FreezeAll;
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"SetRigidbodyState error: {ex.Message}");
        }
    }

    [UsedImplicitly]
    public void OnDestroy()
    {
        IsDestroyed = true;
        IsValid     = false;

        ResetLabyrinthToVanilla();
        ActorOwnershipManager.Unregister(this, registeredActorId);

        if (!LocallyOwned || registeredActorId == 0)
            return;

        if (SystemContext.Instance != null && SystemContext.Instance.SceneLoader != null && SystemContext.Instance.SceneLoader.IsSceneLoadInProgress)
            return;

        try
        {
            // Unloading (zone change, hibernation cleanup) destroys the object but keeps the actor in the world.
            // Only an actor whose model is gone was really destroyed.
            if (GameState.TryGetIdentifiableModel(new ActorId(registeredActorId), out _))
                return;

            ActorManager.Actors.Remove(registeredActorId);
            ActorOwnershipManager.OnActorDestroyed(registeredActorId);

            if (Main.Server.IsRunning || Main.Client.IsConnected)
                Main.SendToAllOrServer(new ActorDestroyPacket { ActorId = new ActorId(registeredActorId) });
        }
        catch (Exception ex)
        {
            SrLogger.LogDebug($"NetworkActor.OnDestroy could not report actor {registeredActorId}: {ex.Message}");
        }
    }
}
