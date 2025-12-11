from talon import Module, actions, app
import os

mod = Module()

@mod.action_class
class Actions:
    def paste_password():
        """Paste password from .secret file"""
        secret_file = os.path.join(os.path.dirname(__file__), ".secret")
        
        try:
            with open(secret_file, "r") as f:
                password = f.read().strip()
                actions.insert(password)
        except FileNotFoundError:
            app.notify("Password file not found", "Create a .secret file in your Talon user directory")
        except Exception as e:
            app.notify("Error reading password", str(e))
