#include <iostream>
#include <vector>
#include <string>

struct LineItem {
    std::string name;
    double price;
    int quantity;
};

double subtotal(const std::vector<LineItem>& items) {
    double total = 0.0;
    for (const auto& item : items) {
        total += item.price * item.quantity;
    }
    return total;
}

// BUG (deliberate): the tax rate is a std::string, so it can't be multiplied by a double.
// The compiler flags grandTotal() below. The clean fix is: const double taxRate = 0.08;
const std::string taxRate = "0.08";

double grandTotal(const std::vector<LineItem>& items) {
    double sub = subtotal(items);
    return sub + sub * taxRate; // error: no operator '*' for 'double' and 'std::string'
}

int main() {
    std::vector<LineItem> cart = {
        {"Widget", 9.99, 3},
        {"Gadget", 19.95, 1},
    };

    std::cout << "Order total: $" << grandTotal(cart) << "\n";
    return 0;
}
