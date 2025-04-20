using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;
using MelonLoader;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.Money;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.Properties;
using Il2CppFishNet.Connection;
using HarmonyLib;
using Il2CppScheduleOne.UI.Phone.Messages;

namespace easy_deals
{
    public static class KeyBindings
    {
        public static KeyCode OpenMenuKey = KeyCode.PageUp;
        public static KeyCode AcceptContractKey = KeyCode.End;
        public static KeyCode NextPeriodKey = KeyCode.UpArrow;
        public static KeyCode PreviousPeriodKey = KeyCode.DownArrow;
        public static KeyCode NextProductKey = KeyCode.RightArrow;
        public static KeyCode PreviousProductKey = KeyCode.LeftArrow;
        public static KeyCode IncreasePrice = KeyCode.Plus;
        public static KeyCode DecreasePrice = KeyCode.Minus;
        
    }
    public class ContractManager
    {
        private static ContractManager _instance;
        public static ContractManager Instance => _instance ?? (_instance = new ContractManager());

        private readonly List<PendingContract> _pendingContracts = new List<PendingContract>();
        private bool _isProcessing = false;
        public float OverallPriceModifier { get; set; } = 10f;
        private readonly Dictionary<string, float> _potentialGains = new Dictionary<string, float>();
        private readonly Dictionary<string, int> _totalQuantities = new Dictionary<string, int>();
        private List<string> _offeredProducts = new List<string> { "All" };
        private bool _productsChanged = false;

        private ContractManager()
        {
            _potentialGains["All"] = 0f;
            _totalQuantities["All"] = 0;
            UpdateCache();
            MelonLogger.Msg("ContractManager initialized with default 'All' values");
        }

        public void AddContract(ProductDefinition product, int quantity, float payment, Customer customer)
        {
            if (product == null || string.IsNullOrEmpty(product.Name))
            {
                MelonLogger.Warning("AddContract failed: Product is null or has no name");
                return;
            }

            var contract = new PendingContract(product, quantity, payment, customer);
            _pendingContracts.Add(contract);
            MelonLogger.Msg($"Contract added: Product={product.Name}, Quantity={quantity}, Payment={payment}, TotalContracts={_pendingContracts.Count}");
            UpdateCache();
            _productsChanged = true;
        }

        public void ProcessContracts(EDealWindow dealWindow, string productName = "All")
        {
            if (_pendingContracts.Count == 0)
            {
                MelonLogger.Msg("No contracts to process");
                return;
            }
            if (_isProcessing)
            {
                MelonLogger.Msg("Already processing contracts");
                return;
            }

            _isProcessing = true;
            MelonCoroutines.Start(ExecuteContracts(dealWindow, productName));
        }

        private IEnumerator ExecuteContracts(EDealWindow dealWindow, string productName)
        {
            var contractsToProcess = productName == "All"
                ? _pendingContracts.ToList()
                : _pendingContracts.Where(c => c.ProductName == productName).ToList();

            _pendingContracts.RemoveAll(c => contractsToProcess.Contains(c));

            foreach (var contract in contractsToProcess)
            {
                bool taskCompleted = false;
                contract.Execute(dealWindow).ContinueWith(t => taskCompleted = true);
                while (!taskCompleted)
                    yield return new WaitForEndOfFrame();
            }

            _isProcessing = false;
            MelonLogger.Msg($"Processed {contractsToProcess.Count} contracts for {productName}");
            UpdateCache();
            _productsChanged = true;
        }

        private void UpdateCache()
        {
            _offeredProducts = new List<string> { "All" };
            _offeredProducts.AddRange(_pendingContracts.Select(c => c.ProductName).Distinct().OrderBy(name => name));

            _potentialGains.Clear();
            _totalQuantities.Clear();
            _potentialGains["All"] = _pendingContracts.Sum(c => c.Payment + OverallPriceModifier);
            _totalQuantities["All"] = _pendingContracts.Sum(c => c.Quantity);

            foreach (var productName in _offeredProducts.Where(n => n != "All"))
            {
                var contracts = _pendingContracts.Where(c => c.ProductName == productName);
                _potentialGains[productName] = contracts.Sum(c => c.Payment + OverallPriceModifier);
                _totalQuantities[productName] = contracts.Sum(c => c.Quantity);
            }

        }

        public void UpdateGainsForModifierChange()
        {
            foreach (var productName in _potentialGains.Keys.ToList())
            {
                var contracts = productName == "All"
                    ? _pendingContracts
                    : _pendingContracts.Where(c => c.ProductName == productName);
                _potentialGains[productName] = contracts.Sum(c => c.Payment + OverallPriceModifier);
            }
        }

        public List<string> GetOfferedProductNames() => _offeredProducts;
        public bool HasProductsChanged() => _productsChanged;
        public void ResetProductsChanged() => _productsChanged = false;
        public float GetPotentialGain(string productName = "All") => _potentialGains.TryGetValue(productName, out float gain) ? gain : 0f;
        public int GetTotalQuantity(string productName = "All") => _totalQuantities.TryGetValue(productName, out int qty) ? qty : 0;
        public List<PendingContract> GetPendingContracts() => _pendingContracts;
    }

    public class PendingContract
    {
        private readonly ProductDefinition _product;
        private readonly int _quantity;
        private readonly float _payment;
        private readonly Customer _customer;

        public string ProductName => _product.Name;
        public float Payment => _payment;
        public int Quantity => _quantity;
        public string CustomerName => _customer.name;
        public ProductDefinition Product => _product;

        public PendingContract(ProductDefinition product, int quantity, float payment, Customer customer)
        {
            _product = product;
            _quantity = quantity;
            _payment = payment;
            _customer = customer;
        }

        public async Task Execute(EDealWindow dealWindow)
        {
            float finalPrice = _payment + ContractManager.Instance.OverallPriceModifier;
            await Task.Delay(100);
            await RunOnMainThread(() => _customer.EvaluateCounteroffer(_product, _quantity, finalPrice));
            await Task.Delay(200);
            await RunOnMainThread(() => _customer.SendCounteroffer(_product, _quantity, finalPrice));
            await Task.Delay(2000);
            await RunOnMainThread(() => _customer.PlayerAcceptedContract(dealWindow));
        }

        private Task RunOnMainThread(Action action)
        {
            var tcs = new TaskCompletionSource<bool>();
            MelonCoroutines.Start(ExecuteOnMainThread(action, tcs));
            return tcs.Task;
        }

        private IEnumerator ExecuteOnMainThread(Action action, TaskCompletionSource<bool> tcs)
        {
            yield return null;
            try
            {
                action();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error executing contract: {ex.Message}");
                tcs.SetException(ex);
            }
        }
    }

    public class MainHandler : MelonMod
    {
        private MessagingManager _messagingManager;
        private ContractManager _contractManager = ContractManager.Instance;
        private TimeManager _timeManager;
        private float _sliderTextTimer = 0f;
        private bool _showSliderText = false;
        private bool _isMenuOpen = false;
        private int _selectedPeriodIndex = 0;
        private int _selectedProductIndex = 0;
        private readonly EDealWindow[] _dealPeriods = { EDealWindow.Morning, EDealWindow.Afternoon, EDealWindow.Night, EDealWindow.LateNight };
        private List<string> _offeredProducts = new List<string> { "All" };

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName == "Main")
            {
                _messagingManager = NetworkSingleton<MessagingManager>.instance;
                _timeManager = NetworkSingleton<TimeManager>.instance;
                MelonLogger.Msg("Mod initialized in Main scene");
            }
        }

        public EDealWindow GetTimePeriod(int time)
        {
            int timeInCycle = time % 2400;
            if (timeInCycle >= 600 && timeInCycle < 1200) return EDealWindow.Morning;
            if (timeInCycle >= 1200 && timeInCycle < 1800) return EDealWindow.Afternoon;
            if (timeInCycle >= 1800 && timeInCycle < 2400) return EDealWindow.Night;
            return EDealWindow.LateNight;
        }

        private (int start, int end) GetPeriodTimes(EDealWindow period)
        {
            switch (period)
            {
                case EDealWindow.Morning:
                    return (600, 1200);
                case EDealWindow.Afternoon:
                    return (1200, 1800);
                case EDealWindow.Night:
                    return (1800, 2400);
                case EDealWindow.LateNight:
                    return (0, 600);
                default:
                    return (0, 600);
            }
        }

        private string FormatGameTime(int ticks)
        {
            float gameHours = ticks / 100f;
            return gameHours >= 1 ? $"{gameHours:F1} hours" : $"{ticks:F0} minutes";
        }

        private string FormatGameTimeForCurrent(int ticks)
        {
            float gameHours = (ticks % 2400) / 100f;
            int hours = (int)gameHours;
            int minutes = (int)((gameHours - hours) * 100);
            return $"{hours:D2}:{minutes:D2}";
        }

        private string GetSliderDisplayText()
        {
            if (_timeManager == null) return "Time not available";

            int currentTime = _timeManager.GetDateTime().time;
            int timeInCycle = currentTime % 2400;
            EDealWindow selectedPeriod = _dealPeriods[_selectedPeriodIndex];
            string periodDisplay = selectedPeriod == GetTimePeriod(currentTime) ? $"{selectedPeriod} (current)" : selectedPeriod.ToString();
            var (start, end) = GetPeriodTimes(selectedPeriod);
            string product = _offeredProducts[_selectedProductIndex];
            int totalQuantity = _contractManager.GetTotalQuantity(product);
            float potentialGain = _contractManager.GetPotentialGain(product);

            int ticksUntilEnd = end > timeInCycle ? end - timeInCycle : (2400 - timeInCycle) + end;
            int ticksUntilStart = start > timeInCycle ? start - timeInCycle : (2400 - timeInCycle) + start;

            return $"Period: <color=#00FFFF>{periodDisplay}</color>\n" +
                   $"Product: <color=#FFFF00>{product} [{totalQuantity} units]</color>\n" +
                   $"Potential Gain: <color=#800080>${potentialGain:F2}</color>\n" +
                   $"Extra Price: <color=#00FF00>${_contractManager.OverallPriceModifier:F2}</color>\n" +
                   $"Current Time: <color=#FFFFFF>{FormatGameTimeForCurrent(currentTime)}</color>\n" +
                   $"Starts in: <color=#FFA500>{FormatGameTime(ticksUntilStart)}</color>\n" +
                   $"Ends in: <color=#FF0000>{FormatGameTime(ticksUntilEnd)}</color>";
        }

        public override void OnUpdate()
        {
            if (_contractManager.HasProductsChanged())
            {
                _offeredProducts = _contractManager.GetOfferedProductNames();
                if (_selectedProductIndex >= _offeredProducts.Count)
                    _selectedProductIndex = 0;
                _contractManager.ResetProductsChanged();
            }

            if (Input.GetKeyDown(KeyBindings.OpenMenuKey))
            {
                _isMenuOpen = !_isMenuOpen;
                _showSliderText = _isMenuOpen;
                if (!_isMenuOpen) _sliderTextTimer = 0f;
            }

            if (Input.GetKeyDown(KeyBindings.PreviousPeriodKey))
            {
                _selectedPeriodIndex = (_selectedPeriodIndex - 1 + _dealPeriods.Length) % _dealPeriods.Length;
                if (!_isMenuOpen) { _showSliderText = true; _sliderTextTimer = 2.5f; }
            }
            else if (Input.GetKeyDown(KeyBindings.NextPeriodKey))
            {
                _selectedPeriodIndex = (_selectedPeriodIndex + 1) % _dealPeriods.Length;
                if (!_isMenuOpen) { _showSliderText = true; _sliderTextTimer = 2.5f; }
            }

            if (_offeredProducts.Count > 1)
            {
                if (Input.GetKeyDown(KeyBindings.PreviousProductKey))
                {
                    _selectedProductIndex = (_selectedProductIndex - 1 + _offeredProducts.Count) % _offeredProducts.Count;
                    if (!_isMenuOpen) { _showSliderText = true; _sliderTextTimer = 2.5f; }
                }
                else if (Input.GetKeyDown(KeyBindings.NextProductKey))
                {
                    _selectedProductIndex = (_selectedProductIndex + 1) % _offeredProducts.Count;
                    if (!_isMenuOpen) { _showSliderText = true; _sliderTextTimer = 2.5f; }
                }
            }
            
            

            if (Input.GetKeyDown(KeyBindings.AcceptContractKey))
            {
                string selectedProduct = _offeredProducts[_selectedProductIndex];
                _contractManager.ProcessContracts(_dealPeriods[_selectedPeriodIndex], selectedProduct);
                if (!_isMenuOpen) { _showSliderText = true; _sliderTextTimer = 2.5f; }
            }

            if (Input.GetKeyDown(KeyBindings.IncreasePrice) || Input.GetKeyDown(KeyCode.Equals))
            {
                _contractManager.OverallPriceModifier += 5f;
                _contractManager.UpdateGainsForModifierChange();
                if (!_isMenuOpen) { _showSliderText = true; _sliderTextTimer = 2.5f; }
            }

            if (Input.GetKeyDown(KeyBindings.DecreasePrice))
            {
                _contractManager.OverallPriceModifier -= 5f;
                _contractManager.UpdateGainsForModifierChange();
                if (!_isMenuOpen) { _showSliderText = true; _sliderTextTimer = 2.5f; }
            }

            //if (Input.GetKeyDown(KeyCode.Keypad2))
            //{
            //    int currentTime = _timeManager.GetDateTime().time;
            //    _timeManager.SetTime(currentTime + 100);
            //}

            if (_showSliderText && !_isMenuOpen)
            {
                _sliderTextTimer -= Time.deltaTime;
                if (_sliderTextTimer <= 0) _showSliderText = false;
            }
        }

        public override void OnGUI()
        {
            if (_showSliderText || _isMenuOpen)
            {
                GUI.color = new Color(0, 0, 0, 0.7f);
                GUI.Box(new Rect(Screen.width / 2 - 160, Screen.height / 2 - 120, 320, 240), "");
                GUI.color = Color.white;
                GUI.skin.label.alignment = TextAnchor.MiddleCenter;
                GUI.skin.label.fontSize = 22;
                GUI.skin.label.richText = true;
                GUI.Label(new Rect(Screen.width / 2 - 150, Screen.height / 2 - 110, 300, 220), GetSliderDisplayText());
            }
        }
    }

    public static class DealsTracker
    {
        public static void AddDealToList(Customer customer)
        {
            var contract = customer.offeredContractInfo;
            if (contract?.Products?.entries == null || contract.Products.entries.Count == 0)
            {
                MelonLogger.Warning($"AddDealToList failed for {customer.name}: No valid contract info");
                return;
            }

            var productEntry = contract.Products.entries[0];
            var productId = productEntry.ProductID;
            var customerName = customer.name;

            if (ContractManager.Instance.GetPendingContracts().Any(c => c.CustomerName == customerName && c.Product.ID == productId))
            {
                MelonLogger.Msg($"Contract already exists for {customerName}, ProductID={productId}");
                return;
            }

            foreach (var product in customer.OrderableProducts)
            {
                if (product.ID == productEntry.ProductID)
                {
                    MelonLogger.Msg($"Adding contract: Product={product.Name}, Quantity={productEntry.Quantity}, Payment={contract.Payment}, Customer={customerName}");
                    ContractManager.Instance.AddContract(product, productEntry.Quantity, contract.Payment, customer);
                    return;
                }
            }
            MelonLogger.Warning($"No matching product found for ProductID={productId} in {customerName}'s OrderableProducts");
        }
    }

    [HarmonyPatch(typeof(Customer), "OfferContract")]
    public static class CustomerPatch
    {
        private static void Postfix(Customer __instance)
        {
            MelonLogger.Msg("Customer.OfferContract patch triggered");
            DealsTracker.AddDealToList(__instance);
        }
    }

    [HarmonyPatch(typeof(Customer), "RpcReader___Observers_SetOfferedContract_4277245194")]
    public static class CustomerClientPatch
    {
        private static void Postfix(Customer __instance)
        {
            MelonLogger.Msg("Customer.RpcReader_SetOfferedContract patch triggered");
            DealsTracker.AddDealToList(__instance);
        }
    }
}