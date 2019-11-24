﻿using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Wabbajack.Common;
using Wabbajack.Lib;

namespace Wabbajack
{
    /// <summary>
    /// Main View Model for the application.
    /// Keeps track of which sub view is being shown in the window, and has some singleton wiring like WorkQueue and Logging.
    /// </summary>
    public class MainWindowVM : ViewModel
    {
        public MainWindow MainWindow { get; }

        public MainSettings Settings { get; }

        private readonly ObservableAsPropertyHelper<ViewModel> _activePane;
        public ViewModel ActivePane => _activePane.Value;

        public ObservableCollectionExtended<string> Log { get; } = new ObservableCollectionExtended<string>();

        [Reactive]
        public RunMode Mode { get; set; }

        private readonly Lazy<CompilerVM> _compiler;
        private readonly Lazy<InstallerVM> _installer;

        public MainWindowVM(RunMode mode, string source, MainWindow mainWindow, MainSettings settings)
        {
            Mode = mode;
            MainWindow = mainWindow;
            Settings = settings;
            _installer = new Lazy<InstallerVM>(() => new InstallerVM(this, source));
            _compiler = new Lazy<CompilerVM>(() => new CompilerVM(this));

            // Set up logging
            Utils.LogMessages
                .ObserveOn(RxApp.TaskpoolScheduler)
                .ToObservableChangeSet()
                .Buffer(TimeSpan.FromMilliseconds(250), RxApp.TaskpoolScheduler)
                .Where(l => l.Count > 0)
                .ObserveOn(RxApp.MainThreadScheduler)
                .FlattenBufferResult()
                .Top(5000)
                .Bind(Log)
                .Subscribe()
                .DisposeWith(CompositeDisposable);

            // Wire mode to drive the active pane.
            // Note:  This is currently made into a derivative property driven by mode,
            // but it can be easily changed into a normal property that can be set from anywhere if needed
            _activePane = this.WhenAny(x => x.Mode)
                .Select<RunMode, ViewModel>(m =>
                {
                    switch (m)
                    {
                        case RunMode.Compile:
                            return _compiler.Value;
                        case RunMode.Install:
                            return _installer.Value;
                        default:
                            return default;
                    }
                })
                .ToProperty(this, nameof(ActivePane));
        }
    }
}
