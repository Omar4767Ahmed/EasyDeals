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
using TestingClass;
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
        private static readonly object _lock = new object();
        private static ContractManager _instance;
        public static ContractManager Instance
        {
            get
            {
                lock (_lock)
                {
                    return _instance ?? (_instance = new ContractManager());
                }
            }
        }

        private readonly List<PendingContract> _pendingContracts = new List<PendingContract>();
        private readonly Queue<PendingContract> _contractPool = new Queue<PendingContract>();
        private bool _isProcessing = false;
        public float OverallPriceModifier { get; set; } = 10f;
        private readonly Dictionary<string, float> _potentialGains = new Dictionary<string, float>();
        private readonly Dictionary<string, int> _totalQuantities = new Dictionary<string, int>();
        private readonly List<string> _offeredProducts = new List<string> { "All" };
        private bool _productsChanged = false;

        private ContractManager()
        {
            _potentialGains["All"] = 0f;
            _totalQuantities["All"] = 0;
            MelonLogger.Msg("ContractManager initialized with default 'All' values");
        }

        public void AddContract(ProductDefinition product, int quantity, float payment, Customer customer)
        {
            if (product == null || string.IsNullOrEmpty(product.Name))
            {
                MelonLogger.Warning("AddContract failed: Product is null or has no name");
                return;
            }

            var contract = GetContract(product, quantity, payment, customer);
            _pendingContracts.Add(contract);

            _potentialGains.TryGetValue("All", out float allGains);
            _potentialGains["All"] = allGains + payment + OverallPriceModifier;

            _totalQuantities.TryGetValue("All", out int allQuantities);
            _totalQuantities["All"] = allQuantities + quantity;

            _potentialGains.TryGetValue(product.Name, out float productGains);
            _potentialGains[product.Name] = productGains + payment + OverallPriceModifier;

            _totalQuantities.TryGetValue(product.Name, out int productQuantities);
            _totalQuantities[product.Name] = productQuantities + quantity;

            if (!_offeredProducts.Contains(product.Name))
            {
                _offeredProducts.Add(product.Name);
                _offeredProducts.Sort();
            }

            MelonLogger.Msg($"Contract added: Product={product.Name}, Quantity={quantity}, Payment={payment}, TotalContracts={_pendingContracts.Count}");
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

            // Process a smaller number of contracts at once
            const int maxContractsPerBatch = 3; // Reduced from 5
            const int delayBetweenContracts = 300; // milliseconds between contracts
            const int delayBetweenBatches = 3000; // milliseconds between batches
            int processedCount = 0;

            _pendingContracts.RemoveAll(c => contractsToProcess.Contains(c));
            RemoveContracts(contractsToProcess);

            // Group by customer but limit the number per customer
            var groupedContracts = contractsToProcess.GroupBy(c => c.CustomerName).ToList();
            foreach (var group in groupedContracts)
            {
                var contracts = group.ToList();
                // Process in smaller batches
                for (int i = 0; i < contracts.Count; i += maxContractsPerBatch)
                {
                    var batch = contracts.Skip(i).Take(maxContractsPerBatch).ToList();
                    yield return MelonCoroutines.Start(ProcessContractBatch(dealWindow, batch, delayBetweenContracts));
                    processedCount += batch.Count;
                    MelonLogger.Msg($"Processed batch of {batch.Count} contracts, total processed: {processedCount}/{contractsToProcess.Count}");

                    // Wait longer between batches to let the game catch up
                    yield return new WaitForSeconds(delayBetweenBatches / 1000f);
                }
            }

            // Return remaining contracts to the pool
            foreach (var contract in contractsToProcess)
            {
                ReturnContract(contract);
            }

            _isProcessing = false;
            MelonLogger.Msg($"Completed processing {processedCount} contracts for {productName}");
            _productsChanged = true;
        }

        private IEnumerator ProcessContractBatch(EDealWindow dealWindow, List<PendingContract> contracts, int delayBetweenContracts)
        {
            foreach (var contract in contracts)
            {
                bool taskCompleted = false;
                float timeout = Time.time + 10f;
                MelonLogger.Msg($"Processing contract for {contract.ProductName}");

                contract.Execute(dealWindow).ContinueWith(t => {
                    taskCompleted = true;
                    if (t.IsFaulted)
                        MelonLogger.Error($"Contract execution failed: {t.Exception?.InnerException?.Message}");
                });

                // Wait for completion or timeout
                while (!taskCompleted && Time.time < timeout)
                {
                    yield return null;
                }

                // Increase delay between individual contracts
                yield return new WaitForSeconds(delayBetweenContracts / 1000f);
            }
        }


        private void RemoveContracts(List<PendingContract> contracts)
        {
            foreach (var contract in contracts)
            {
                _potentialGains.TryGetValue("All", out float allGains);
                _potentialGains["All"] = allGains - (contract.Payment + OverallPriceModifier);

                _totalQuantities.TryGetValue("All", out int allQuantities);
                _totalQuantities["All"] = allQuantities - contract.Quantity;

                _potentialGains.TryGetValue(contract.ProductName, out float productGains);
                _potentialGains[contract.ProductName] = productGains - (contract.Payment + OverallPriceModifier);

                _totalQuantities.TryGetValue(contract.ProductName, out int productQuantities);
                _totalQuantities[contract.ProductName] = productQuantities - contract.Quantity;

                if (!_pendingContracts.Any(c => c.ProductName == contract.ProductName))
                {
                    _potentialGains.Remove(contract.ProductName);
                    _totalQuantities.Remove(contract.ProductName);
                    _offeredProducts.Remove(contract.ProductName);
                }
            }
        }

        public void UpdateGainsForModifierChange()
        {
            _potentialGains.Clear();
            _potentialGains["All"] = _pendingContracts.Sum(c => c.Payment + OverallPriceModifier);

            foreach (var productName in _offeredProducts.Where(n => n != "All"))
            {
                _potentialGains[productName] = _pendingContracts
                    .Where(c => c.ProductName == productName)
                    .Sum(c => c.Payment + OverallPriceModifier);
            }
        }

        public List<string> GetOfferedProductNames() => _offeredProducts;
        public bool HasProductsChanged() => _productsChanged;
        public void ResetProductsChanged() => _productsChanged = false;

        public float GetPotentialGain(string productName = "All")
        {
            _potentialGains.TryGetValue(productName, out float gain);
            return gain;
        }

        public int GetTotalQuantity(string productName = "All")
        {
            _totalQuantities.TryGetValue(productName, out int qty);
            return qty;
        }

        public List<PendingContract> GetPendingContracts() => _pendingContracts;

        private PendingContract GetContract(ProductDefinition product, int quantity, float payment, Customer customer)
        {
            PendingContract contract;
            if (_contractPool.Count > 0)
            {
                contract = _contractPool.Dequeue();
                contract.Reset(product, quantity, payment, customer);
            }
            else
            {
                contract = new PendingContract(product, quantity, payment, customer);
            }
            return contract;
        }

        private void ReturnContract(PendingContract contract)
        {
            _contractPool.Enqueue(contract);
        }
    }

    public class PendingContract
    {
        private ProductDefinition _product;
        private int _quantity;
        private float _payment;
        private Customer _customer;

        public string ProductName => _product != null ? _product.Name : "Null Product";
        public float Payment => _payment;
        public int Quantity => _quantity;
        public string CustomerName => _customer != null ? _customer.name : "Null Customer";
        public ProductDefinition Product => _product;

        public PendingContract(ProductDefinition product, int quantity, float payment, Customer customer)
        {
            Reset(product, quantity, payment, customer);
        }

        public void Reset(ProductDefinition product, int quantity, float payment, Customer customer)
        {
            _product = product;
            _quantity = quantity;
            _payment = payment;
            _customer = customer;
        }

        public async Task Execute(EDealWindow dealWindow)
        {
            if (_customer == null || _product == null)
            {
                MelonLogger.Warning($"Skipping contract execution: Invalid customer or product");
                return;
            }

            float finalPrice = _payment + ContractManager.Instance.OverallPriceModifier;

            // Use a safer execution pattern with timeouts and retries
            int maxRetries = 2;
            int currentRetry = 0;

            while (currentRetry <= maxRetries)
            {
                try
                {
                    MelonLogger.Msg($"Step 1: EvaluateCounteroffer for {ProductName} (attempt {currentRetry + 1})");
                    bool step1Success = false;

                    // Add timeout for safety
                    var task1 = RunOnMainThread(() => {
                        _customer.EvaluateCounteroffer(_product, _quantity, finalPrice);
                        return true;
                    });

                    // Wait with timeout
                    if (await Task.WhenAny(task1, Task.Delay(5000)) == task1)
                        step1Success = await task1;
                    else
                        throw new TimeoutException("EvaluateCounteroffer timed out");

                    await Task.Delay(200);

                    MelonLogger.Msg($"Step 2: SendCounteroffer for {ProductName}");
                    bool step2Success = false;

                    var task2 = RunOnMainThread(() => {
                        _customer.SendCounteroffer(_product, _quantity, finalPrice);
                        return true;
                    });

                    if (await Task.WhenAny(task2, Task.Delay(5000)) == task2)
                        step2Success = await task2;
                    else
                        throw new TimeoutException("SendCounteroffer timed out");

                    await Task.Delay(1500);

                    MelonLogger.Msg($"Step 3: PlayerAcceptedContract for {ProductName}");

                    var task3 = RunOnMainThread(() => {
                        _customer.PlayerAcceptedContract(dealWindow);
                        _customer.NPC.MSGConversation.ClearResponses(true);
                        return true;
                    });

                    if (await Task.WhenAny(task3, Task.Delay(5000)) != task3)
                        throw new TimeoutException("PlayerAcceptedContract timed out");

                    // If we got here, all steps completed successfully
                    MelonLogger.Msg($"Contract successfully executed for {ProductName}");
                    return;
                }
                catch (Exception ex)
                {
                    currentRetry++;
                    MelonLogger.Error($"Contract execution attempt {currentRetry} failed for {ProductName}: {ex.Message}");

                    if (currentRetry > maxRetries)
                    {
                        MelonLogger.Error($"Failed to execute contract after {maxRetries + 1} attempts");
                        return;
                    }

                    // Wait before retry
                    await Task.Delay(1000);
                }
            }
        }

        private Task<T> RunOnMainThread<T>(Func<T> action)
        {
            var tcs = new TaskCompletionSource<T>();
            MelonCoroutines.Start(ExecuteOnMainThread(action, tcs));
            return tcs.Task;
        }

        private IEnumerator ExecuteOnMainThread<T>(Func<T> action, TaskCompletionSource<T> tcs)
        {
            yield return null;
            try
            {
                T result = action();
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
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
            if (_offeredProducts == null || _offeredProducts.Count == 0) return "No products available";
            if (_selectedProductIndex < 0 || _selectedProductIndex >= _offeredProducts.Count) return "Invalid product selection";
            
    
        if (_selectedPeriodIndex < 0 || _selectedPeriodIndex >= _dealPeriods.Length) return "Invalid period selection";

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
                // Ensure _selectedProductIndex is valid
                if (_offeredProducts.Count == 0)
                {
                    _offeredProducts.Add("All");
                    _selectedProductIndex = 0;
                }
                else if (_selectedProductIndex >= _offeredProducts.Count)
                {
                    _selectedProductIndex = _offeredProducts.Count - 1;
                }
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

            if (Input.GetKeyDown(KeyCode.Keypad2))
            {
                int currentTime = _timeManager.GetDateTime().time;
                _timeManager.SetTime(currentTime + 100);
            }

            if (Input.GetKeyDown(KeyCode.Keypad3))
            {
                try
                {
                    TestingClass.TestingClass.GenerateRandomQuests(10);
                }
                catch (Exception ex)
                {
                    MelonLogger.Error(ex);
                }
            }

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
        // Cache for product definitions keyed by ProductID
        private static readonly Dictionary<string, ProductDefinition> _productCache = new Dictionary<string, ProductDefinition>();

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

            // Check if a contract already exists
            if (ContractManager.Instance.GetPendingContracts()
                .Any(c => c.CustomerName == customerName && c.Product.ID == productId))
            {
                MelonLogger.Msg($"Contract already exists for {customerName}, ProductID={productId}");
                return;
            }

            // Try to get from cache
            if (!_productCache.TryGetValue(productId, out var productDef))
            {
                // Cache miss: manually search in customer's orderable products
                foreach (var p in customer.OrderableProducts)
                {
                    if (p.ID == productId)
                    {
                        productDef = p;
                        _productCache[productId] = productDef; // Cache it for future use
                        
                        break;
                    }
                }

                if (productDef == null)
                {
                    MelonLogger.Warning($"No matching product found for ProductID={productId} in {customerName}'s OrderableProducts");
                    return;
                }
            }


            // Add contract using cached or found definition
            MelonLogger.Msg($"Adding contract: Product={productDef.Name}, Quantity={productEntry.Quantity}, Payment={contract.Payment}, Customer={customerName}");
            ContractManager.Instance.AddContract(productDef, productEntry.Quantity, contract.Payment, customer);
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

    [HarmonyPatch(typeof(Customer), "Load")]
    public static class CustomerStartPatch
    {
        private static void Postfix(Customer __instance)
        {
            try
            {
                // Check if this customer has an active contract offer
                if (__instance != null && __instance.offeredContractInfo != null)
                {
                    MelonLogger.Msg($"CustomerStartPatch: Found active contract for {__instance.name}");
                    DealsTracker.AddDealToList(__instance);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error in CustomerStartPatch for {(__instance != null ? __instance.name : "unknown")}: {ex.Message}");
            }
        }
    }
}