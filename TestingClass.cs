using System;
using System.Collections.Generic;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Product;
using Il2CppSystem.Collections.Generic;
using Il2CppScheduleOne.Quests;

namespace TestingClass
{
    static class TestingClass
    {
        // Static field to cache a single product globally across the class
        private static ProductDefinition cachedProduct = null;

        static public void GenerateRandomQuests(int numberOfQuests)
        {
            // Create a new list to avoid modifying the original collection during iteration
            var UnlockedCustomers = new Il2CppSystem.Collections.Generic.List<Customer>();
            foreach (var customer in Customer.UnlockedCustomers)
            {
                UnlockedCustomers.Add(customer);
            }
            MelonLoader.MelonLogger.Msg("we have : " + UnlockedCustomers.Count);

            if (UnlockedCustomers == null || UnlockedCustomers.Count == 0) return;

            Random rng = new Random();

            while (numberOfQuests > 0)
            {
                if (UnlockedCustomers.Count == 0) return;

                // Correct use of Count
                int randomNumber = rng.Next(0, UnlockedCustomers.Count);
                Customer currentUnlockedCustomer = UnlockedCustomers[randomNumber];

                // If the customer is not valid, continue to the next one
                if (currentUnlockedCustomer == null ||
                    currentUnlockedCustomer.offeredContractInfo != null ||
                    currentUnlockedCustomer.DefaultDeliveryLocation == null)
                {
                    UnlockedCustomers.RemoveAt(randomNumber);
                    continue;
                }

                // Get orderable products for the current customer
                Il2CppSystem.Collections.Generic.List<ProductDefinition> products = currentUnlockedCustomer.OrderableProducts;

                // Cache the first product if no product has been cached yet
                if (cachedProduct == null && products.Count > 0)
                {
                    cachedProduct = products[0]; // Cache the first available product
                }

                // If the customer doesn't have orderable products, use the cached product
                ProductDefinition randomProduct = null;

                if (products.Count > 0)
                {
                    randomProduct = products[rng.Next(0, products.Count)];
                }
                else if (cachedProduct != null)
                {
                    randomProduct = cachedProduct; // Use the cached product if no products are available
                }

                if (randomProduct == null)
                {
                    // Skip if no valid product could be selected
                    UnlockedCustomers.RemoveAt(randomNumber);
                    continue;
                }

                // Proceed to create and offer a contract with the selected product
                ProductList _myProducts = new ProductList();
                _myProducts.entries = new Il2CppSystem.Collections.Generic.List<ProductList.Entry>();

                ProductList.Entry _myEntry = new ProductList.Entry
                {
                    ProductID = randomProduct.ID,
                    Quantity = 2,
                    Quality = Il2CppScheduleOne.ItemFramework.EQuality.Standard
                };

                _myProducts.entries.Add(_myEntry);

                QuestWindowConfig _myConfig = new QuestWindowConfig
                {
                    WindowStartTime = 0,
                    WindowEndTime = 600
                };

                string deliveryGUID = currentUnlockedCustomer.DefaultDeliveryLocation.GUID.ToString();

                ContractInfo _contractInfo = new ContractInfo(
                    100,
                    _myProducts,
                    deliveryGUID,
                    _myConfig,
                    true,
                    900,
                    numberOfQuests,
                    false
                );

                // Offer the contract (assuming OfferContract is implemented in your API)
                currentUnlockedCustomer.OfferContract(_contractInfo);

                // Remove the customer from the list after processing
                UnlockedCustomers.RemoveAt(randomNumber);

                // Decrease the number of quests
                numberOfQuests--;
            }
        }
    }
}
