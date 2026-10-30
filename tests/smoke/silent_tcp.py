"""Accepts TCP connections on the given port and never answers: an open port that is not the expected service."""
import socket, sys, threading
server = socket.socket(); server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
server.bind(("127.0.0.1", int(sys.argv[1]))); server.listen()
held = []
while True:
    connection, _ = server.accept(); held.append(connection)
