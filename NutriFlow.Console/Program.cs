


Console.WriteLine("NutriFlow запущен.");

List<Product> spisokEnd = new();
Product Beef = new Product();
Product Gerchka = new Product();


public class NutritionPer100g
{
    public decimal Calories { get; set; }
    public decimal Protein { get; set; }
    public decimal Fat { get; set; }
    public decimal Carbs { get; set; }

}


public class Product
{
    public string Name { get; set; }
    public decimal Weight { get; set; }
    public NutritionPer100g Nutrition { get; set; }

    public NutritionTotal CalculateTotal()
    {
        decimal multiplier = Weight / 100;

        NutritionTotal total = new NutritionTotal();

        total.Calories = Nutrition.Calories * multiplier;
        total.Protein = Nutrition.Protein * multiplier;
        total.Fat = Nutrition.Fat * multiplier;
        total.Carbs = Nutrition.Carbs * multiplier;

        return total;
    }

}
public class NutritionTotal
{
    public decimal Calories { get; set; }
    public decimal Protein { get; set; }
    public decimal Fat { get; set; }
    public decimal Carbs { get; set; }
}

