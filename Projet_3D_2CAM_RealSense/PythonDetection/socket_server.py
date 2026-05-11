import socket
import json

class SocketServer:
    def __init__(self, host='127.0.0.1', port=5000):
        self.server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.server.bind((host, port))
        self.server.listen(1)
        print(f"Serveur Python sur {host}:{port}, en attente du C#...")
        self.conn, addr = self.server.accept()
        print(f"Application C# connectée via {addr}")

    def send(self, data):
        message = json.dumps(data) + "\n"
        try:
            self.conn.sendall(message.encode('utf-8'))
        except:
            print("Connexion perdue. En attente de reconnexion...")
            self.conn, _ = self.server.accept()