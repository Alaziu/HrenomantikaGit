using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Windows;
using System.Windows.Controls;
using Hangfire;
using Hangfire.MemoryStorage;
using LibGit2Sharp;
using Microsoft.WindowsAPICodePack.Dialogs;
using Owin;
using Serilog;

namespace Hrenomantika.Ui
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        private BackgroundJobServer server;
        private Repository repo;
        private bool isStarted;
        private static int countBrunches;

        public static MainWindow Instance;
        
        public MainWindow()
        {
            InitializeComponent();
            InitLog();
            InitHangfire();
            Instance = this;
        }

        private void InitLog()
        {
            var log = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File("hr.log")
                .CreateLogger();
            Log.Logger = log;
        }

        private void InitHangfire()
        {
            GlobalConfiguration.Configuration.UseSerilogLogProvider().UseMemoryStorage();
            
            var opt = new BackgroundJobServerOptions();
            
            JobStorage.Current = new MemoryStorage(new MemoryStorageOptions()
            {
                JobExpirationCheckInterval = TimeSpan.FromMinutes(1),
                CountersAggregateInterval = TimeSpan.FromMinutes(1),
                FetchNextJobTimeout = TimeSpan.FromMinutes(1)
            });
            
            server = new BackgroundJobServer(opt, JobStorage.Current);
        }
        
        private void StartRepo(object sender, System.Windows.RoutedEventArgs e)
        {
            if (isStarted)
            {
                RecurringJob.RemoveIfExists("backup-1");
                repo?.Dispose();
                StartButton.Content = "Старт";
                isStarted = false;
                ListCommit.Items.Clear();
                return;
            }

            if (!EnsureDirectoryIsExists())
            {
                MessageBox.Show("Путь хрень");
                return;
            }

            var path = GetDirectory();

            EnsureGitIsInit(path);
            CreateRepoConnection(path);

            LoadBrunches();
            LoadCommits();
            
            //MessageBox.Show("AddTask");
            RecurringJob.AddOrUpdate("backup-1", () => ScheduleMethod(), Cron.Minutely);
            RecurringJob.Trigger("backup-1");
            
            StartButton.Content = "Стоп";
            
            isStarted = true;
        }

        private void LoadBrunches()
        {
            countBrunches = repo.Branches.Max(s =>
            {
                var name = s.FriendlyName;
                var num = 1;
                if (name != "master")
                    num = Convert.ToInt32(name);
                return num;
            }) + 1;
        }

        private void LoadCommits()
        {
            var commitsList = new List<Commit>();
            
            PrintBrunch(repo.Head);
            
            var brunches = repo.Branches;
            foreach (var brunch in brunches)
            {
                if (brunch.CanonicalName != repo.Head.CanonicalName)
                {
                    PrintBrunch(brunch);
                }
            }
            
            void PrintBrunch(Branch branch)
            {
                var title = new ListBoxItem();
                    
                title.Content = $"--- Brunch: {branch.FriendlyName}";
                title.Height = 25;
                ListCommit.Items.Add(title);
                    
                var commits = branch.Commits;

                foreach (var commit in commits)
                {
                    if (commitsList.Contains(commit) && commits.First().Sha != commit.Sha)
                        continue;
                    commitsList.Add(commit);
                    
                    var listboxitem = new CommitItem();
                    listboxitem.Commit = commit;
                    listboxitem.Content = commit.Message;
                    listboxitem.Height = 25;

                    ListCommit.Items.Add(listboxitem);
                }
            }
        }

        public static void ScheduleMethod()
        {
            Instance.Schedule();
        }

        public void Schedule()
        {
            Info();
            //MessageBox.Show("Apply");
            if (repo.Diff.Compare<TreeChanges>(repo.Head.Tip.Tree, DiffTargets.Index | DiffTargets.WorkingDirectory).Count > 0)
            {
                Commands.Stage(repo, "*");
                
                Signature author = new Signature("Hrenomantika", "@yashik", DateTime.Now);
                Signature committer = author;

                Commit commit = repo.Commit(DateTime.Now.ToLongDateString() + " " + DateTime.Now.ToLongTimeString(), author, committer);
                //MessageBox.Show("Schedule before add commit");
                
                Application.Current.Dispatcher.Invoke(() => { 
                    AddNewCommitToList(commit.Message);
                });
            }
        }

        private void AddNewCommitToList(string commit)
        {
            ListCommit.Items.Clear();
            LoadCommits();
        }

        public static void AddCommitToList(Commit commit)
        {
            Instance.ListCommit.Items.Clear();
            Instance.LoadCommits();
        }

        private void CreateRepoConnection(DirectoryInfo path)
        {
            repo = new Repository(path.FullName);
        }

        private void EnsureGitIsInit(DirectoryInfo path)
        {
            var t = Repository.Init(path.FullName);
            using (var repo = new Repository(path.FullName))
            {
                if (!repo.Commits.Any())
                {
                    var content = "by YashikMoloka";
                    File.WriteAllText(Path.Combine(repo.Info.WorkingDirectory, ".hr"), content);

                    // Stage the file
                    repo.Index.Add(".hr");
                    repo.Index.Write();
                    // Create the committer's signature and commit
                    Signature author = new Signature("Hrenomantika", "@yashik", DateTime.Now);
                    Signature committer = author;

                    // Commit to the repository
                    Commit commit = repo.Commit("Initial", author, committer);
                }
            }
        }

        private DirectoryInfo GetDirectory()
        {
            return new DirectoryInfo(PathToRepo.Text);
        }

        private bool EnsureDirectoryIsExists()
        {
            var path = PathToRepo.Text;
            return Directory.Exists(path);
        }

        private void LoadCommit(object sender, System.Windows.RoutedEventArgs e)
        {
            if (!(ListCommit.SelectedItem is CommitItem))
                return;
            var commit = ((CommitItem)ListCommit.SelectedItem).Commit;
            //Commands.Checkout(repo, commit);

            var head = repo.Head.Commits.First();
            var headscommits = repo.Branches.Select(s => s.Commits.First().Sha).ToList();
            if (headscommits.Contains(commit.Sha))
            {
                var brunch = repo.Branches.First(s => s.Commits.First().Sha == commit.Sha);
                Commands.Checkout(repo, brunch);
            }
            else
            {
                var newBranch = repo.CreateBranch(countBrunches.ToString(), commit);
                Commands.Checkout(repo, newBranch);
                countBrunches++;
            }
            
            ListCommit.Items.Clear();
            LoadCommits();
        }
        
        private void SelectFolder(object sender, System.Windows.RoutedEventArgs e)
        {
            var dialog = new CommonOpenFileDialog();
            dialog.IsFolderPicker = true;
            CommonFileDialogResult result = dialog.ShowDialog();
            if (result == CommonFileDialogResult.Ok)
            {
                PathToRepo.Text = dialog.FileName;
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            repo?.Dispose();
            server?.Dispose();
        }
        
        
        private void Info()
        {
            //var com = repo.Commits;
        }
    }

    public class CommitItem : ListBoxItem
    {
        public Commit Commit;
    }
}