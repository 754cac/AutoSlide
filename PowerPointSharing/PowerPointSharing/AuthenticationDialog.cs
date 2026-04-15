using System;
using System.Drawing;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Http;

namespace PowerPointSharing
{
    public class AuthenticationDialog : Form
    {
        private Label _emailLabel = null!;
        private TextBox _emailTextBox = null!;
        private Label _passwordLabel = null!;
        private TextBox _passwordTextBox = null!;
        private Button _loginButton = null!;
        private Label _statusLabel = null!;

        private Label _courseLabel = null!;
        private ComboBox _courseComboBox = null!;
        private Button _startSessionButton = null!;

        private readonly PresentationApiClient _apiClient;
        public string SelectedCourseId { get; private set; } = string.Empty;

        public string SelectedCourseName { get; private set; } = string.Empty;

        public string AuthToken { get; private set; } = string.Empty;

        public AuthenticationDialog(string backendBaseUrl)
        {
            _apiClient = new PresentationApiClient(backendBaseUrl);
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Size = new Size(350, 350);
            this.Text = "AutoSlide Publisher - Login";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;

            _emailLabel = new Label { Text = "Email:", Location = new Point(20, 20), AutoSize = true };
            _emailTextBox = new TextBox { Location = new Point(20, 45), Width = 290, Text = "teacher@polyu.edu.hk" };

            _passwordLabel = new Label { Text = "Password:", Location = new Point(20, 80), AutoSize = true };
            _passwordTextBox = new TextBox { Location = new Point(20, 105), Width = 290, PasswordChar = '*' };

            _loginButton = new Button { Text = "Login", Location = new Point(20, 140), Width = 290, Height = 30, BackColor = Color.LightGray };
            _loginButton.Click += OnLoginButtonClicked;

            _statusLabel = new Label { Location = new Point(20, 175), Width = 290, ForeColor = Color.Red };

            var divider = new Label { BorderStyle = BorderStyle.Fixed3D, Location = new Point(20, 200), Width = 290, Height = 2 };

            _courseLabel = new Label { Text = "Select Course:", Location = new Point(20, 215), AutoSize = true, Enabled = false };
            _courseComboBox = new ComboBox { Location = new Point(20, 240), Width = 290, DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };

            _startSessionButton = new Button { Text = "Start Presentation", Location = new Point(20, 275), Width = 290, Height = 35, BackColor = Color.LightBlue, Enabled = false };
            _startSessionButton.Click += OnStartSessionButtonClicked;

            this.Controls.Add(_emailLabel);
            this.Controls.Add(_emailTextBox);
            this.Controls.Add(_passwordLabel);
            this.Controls.Add(_passwordTextBox);
            this.Controls.Add(_loginButton);
            this.Controls.Add(_statusLabel);
            this.Controls.Add(divider);
            this.Controls.Add(_courseLabel);
            this.Controls.Add(_courseComboBox);
            this.Controls.Add(_startSessionButton);
        }

        private async void OnLoginButtonClicked(object sender, EventArgs e)
        {
            _statusLabel.Text = "Logging in...";
            _statusLabel.ForeColor = Color.Blue;
            _loginButton.Enabled = false;

            try
            {
                var token = await _apiClient.LoginAsync(_emailTextBox.Text, _passwordTextBox.Text);

                if (!string.IsNullOrEmpty(token))
                {
                    var nonNullToken = token!;
                    this.AuthToken = nonNullToken;
                    _apiClient.SetToken(nonNullToken);
                    _statusLabel.Text = "Login Successful! Fetching courses...";
                    _statusLabel.ForeColor = Color.Green;

                    await LoadAvailableCourses();
                }
                else
                {
                    _statusLabel.Text = "Invalid credentials.";
                    _statusLabel.ForeColor = Color.Red;
                    _loginButton.Enabled = true;
                }
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Error: " + ex.Message;
                _statusLabel.ForeColor = Color.Red;
                _loginButton.Enabled = true;
            }
        }

        private async Task LoadAvailableCourses()
        {
            try
            {
                _statusLabel.Text = "Fetching courses...";
                _statusLabel.ForeColor = Color.Blue;

                var courses = await _apiClient.GetCoursesAsync();

                if (courses == null || courses.Count == 0)
                {
                    _statusLabel.Text = "Login OK, but no courses found for this user.";
                    _statusLabel.ForeColor = Color.Orange;
                    return;
                }

                _courseComboBox.DataSource = courses;
                _courseComboBox.DisplayMember = "Name";
                _courseComboBox.ValueMember = "Id";

                _courseLabel.Enabled = true;
                _courseComboBox.Enabled = true;
                _startSessionButton.Enabled = true;

                _emailTextBox.Enabled = false;
                _passwordTextBox.Enabled = false;
                _loginButton.Visible = false;

                _statusLabel.Text = "Ready to start.";
                _statusLabel.ForeColor = Color.Green;
            }
            catch (HttpRequestException httpEx)
            {
                _statusLabel.Text = $"Network Error: {httpEx.Message}"; 
                _statusLabel.ForeColor = Color.Red;

                MessageBox.Show($"Full Error:\n{httpEx}", "Connection Failed");
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Error: {ex.Message}";
                _statusLabel.ForeColor = Color.Red;
            }
        }

        private void OnStartSessionButtonClicked(object sender, EventArgs e)
        {
            if (_courseComboBox.SelectedValue == null) return;

            SelectedCourseId = _courseComboBox.SelectedValue.ToString();

            var selectedCourse = (PresentationApiClient.CourseInfo)_courseComboBox.SelectedItem;
            SelectedCourseName = selectedCourse.Name;

            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
