using CoilOptimizer.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace CoilOptimizer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            vm = new LayoutVM();
            layoutView.DataContext = vm;

            // react to manual drags
            vm.LayoutChanged += (s, e) =>
            {
                // e.Shape, e.InchesX, e.InchesY  -> apply your snap/validation rules,
                // then vm.Rebuild() once the underlying model is updated.
            };
        }

        private LayoutVM vm;

        private async void  Button_Click(object sender, RoutedEventArgs e)
        {
            await Test();
        }

        private async Task Test()
        {
            var input = new OptimizationInput();
            input.Constraints.SheetWidthConstraint.Value = 40.875m;
            input.Constraints.MinPartWidthConstraint.Value = 2.559m;
            input.Constraints.MinOutsidePartWidthConstraint.Value = 0.197m;
            input.Constraints.MinPartHeightConstraint.Value = 0.0m;
            input.Options.MaxKnives = 8;
            input.Options.ScrapWidth = 3.0m;
            input.Options.Timeout = TimeSpan.FromMilliseconds(300);
            var sw = Stopwatch.StartNew();

            if (Job1.IsChecked == true)
            {
                input.Parts = new List<PartCut>
                {
                    new PartCut { Width = 12m,     Length = 147m, Quantity = 21 },
                    new PartCut { Width = 14.375m, Length = 123m, Quantity = 7  },
                    new PartCut { Width = 4.313m,  Length = 123m, Quantity = 49 },
                };
            }
            if (Job2.IsChecked == true)
            {

                input.Parts = new List<PartCut>
                {
                    new PartCut { Width = 3.5m, Length = 123, Quantity = 2 },
                    new PartCut { Width = 6.0m, Length = 123, Quantity = 12 },
                    new PartCut { Width = 12.0m, Length = 123, Quantity = 6 },
                    new PartCut { Width = 16.0m, Length = 123, Quantity = 8 },
                };
            }
            if (Job3.IsChecked == true)
            {
                input.Parts = new List<PartCut>
                {
                    new PartCut { Width = 3.0m, Length = 96, Quantity = 48 },
                    new PartCut { Width = 6.75m, Length = 123, Quantity = 48 },
                    new PartCut { Width = 10.75m, Length = 96, Quantity = 96 }
                };
            }

            var engine = new CoilOptimizerEngine();
            engine.BetterSolutionFound += (s, e) =>
            {
                // Marshal to UI thread in WPF (Dispatcher.Invoke) before touching UI.
                Dispatcher.Invoke(() => {
                    Console.WriteLine(
                        $"{(e.IsFinal ? "FINAL " : "better")} " +
                        $"Len={e.Result.TotalLength} Waste%={e.Result.WastePercentage} " +
                        $"Patterns={e.Result.PatternCount}, {sw.ElapsedMilliseconds}ms");

                    vm.SetResult(e.Result, input.Constraints.SheetWidthConstraint.Value);
                    
                });
            };

            CuttingResult fast = engine.Optimize(input);
            Console.WriteLine($"fast Len={fast.TotalLength} Waste%={fast.WastePercentage} Patterns={fast.PatternCount}, {sw.ElapsedMilliseconds}ms");
            Dispatcher.Invoke(() => vm.SetResult(fast, input.Constraints.SheetWidthConstraint.Value));
        }

        private void Job1_Checked(object sender, RoutedEventArgs e)
        {

        }
    }
}
