#include <iostream>
#include <vector>
#include <string>

struct LineItem {
    std::string name;
    double price;
    int quantity;
};

static double subtotal(const std::vector<LineItem>& items) {
    double total = 0.0;
    for (const auto& item : items) {
        total += item.price * item.quantity;
    }
    return total;
}

const double taxRate = 0.08;
static double grandTotal(const std::vector<LineItem>& items) {
    double sub = subtotal(items);
    return sub + sub * taxRate;
}

static double applyDiscount(double price, int percent) {
    return price - (price * percent / 100);
}

int main() {
    std::vector<LineItem> cart = {
        {"Widget", 9.99, 3},
        {"Gadget", 19.95, 1},
    };

    std::cout << "Order total: $" << grandTotal(cart) << "\n";

    // Deliberate Bugs
    double discountedTotal = applyDiscount(grandTotal(cart) 10);
    std::cout < "Discounted total: $" << discountedTotal << "\n";
    return 0
}
