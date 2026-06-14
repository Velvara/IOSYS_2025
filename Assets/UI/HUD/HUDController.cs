using UnityEngine;

namespace StarterAssets
{
    /// <summary>
    /// Owns the HUD prefab and coordinates between widgets.
    /// Finds the IStaminaData source in the scene and hands it down
    /// to SurvivalBarsController.
    ///
    /// For character switching, call BindCharacter() with the new
    /// character's GameObject -- all widgets rebind automatically.
    /// </summary>
    public class HUDController : MonoBehaviour
    {
        // -- Inspector --
        [Tooltip("Assign if the player GameObject is known at edit time. " +
                 "If left empty, HUDController will search the scene at Start.")]
        [SerializeField] private GameObject _playerObject;

        // -- Components --
        private SurvivalBarsController _survivalBars;

        // -- Lifecycle --

        private void Awake()
        {
            _survivalBars = GetComponent<SurvivalBarsController>();

            if (_survivalBars == null)
                Debug.LogWarning("HUDController: SurvivalBarsController not found on this GameObject.", this);
        }

        private void Start()
        {
            // If no player was assigned in the Inspector, search the scene.
            if (_playerObject == null)
                _playerObject = GameObject.FindWithTag("Player");

            if (_playerObject == null)
            {
                Debug.LogError("HUDController: no player object found. " +
                               "Assign it in the Inspector or tag your player GameObject as 'Player'.", this);
                return;
            }

            BindCharacter(_playerObject);
        }

        // -- Public API --

        /// <summary>
        /// Binds all HUD widgets to the given character's components.
        /// Call this when the player switches characters.
        /// </summary>
        public void BindCharacter(GameObject character)
        {
            var staminaData = character.GetComponent<IStaminaData>();

            if (staminaData == null)
                Debug.LogWarning($"HUDController: {character.name} has no IStaminaData component.", this);

            if (_survivalBars != null)
                _survivalBars.Bind(staminaData);
        }
    }
}